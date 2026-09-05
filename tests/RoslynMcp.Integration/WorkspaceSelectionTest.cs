using System.Diagnostics;
using System.Text.Json;
using RoslynMcp.Core.Helpers;
using Xunit;

namespace RoslynMcp.Integration;

[Collection("workspace")]
public sealed class WorkspaceSelectionTest : IDisposable
{
    private readonly string _targetRoot = Path.Combine(
        Path.GetTempPath(),
        "RoslynMcpTests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task SelectorsReplaceTheActiveSolutionAndRemainSticky()
    {
        Directory.CreateDirectory(Path.Combine(_targetRoot, ".git"));
        var targetSolution = Path.Combine(_targetRoot, "Target.sln");
        File.WriteAllText(
            targetSolution,
            "Microsoft Visual Studio Solution File, Format Version 12.00\r\n" +
            "# Visual Studio Version 17\r\n" +
            "Project(\"{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}\") = \"Target\", \"Target.csproj\", \"{60483A71-E0B3-43E3-82AB-F69776481045}\"\r\n" +
            "EndProject\r\nGlobal\r\nEndGlobal\r\n");
        File.WriteAllText(
            Path.Combine(_targetRoot, "Target.csproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
        File.WriteAllText(
            Path.Combine(_targetRoot, "Class1.cs"),
            "public sealed class StickyTargetMarker { }");

        var solutionDir = FindSolutionDir();
        var serverExe = FindServerExe(solutionDir);
        Assert.True(File.Exists(serverExe), $"Server exe not found: {serverExe}. Run 'dotnet build' first.");

        using var server = StartServer(
            serverExe,
            solutionDir,
            Path.Combine(solutionDir, "RoslynMcp.sln"));
        var stderr = server.StandardError.ReadToEndAsync();

        try
        {
            var initialize = await SendRequestAsync(server, 1, "initialize", new
            {
                protocolVersion = "2024-11-05",
                capabilities = new { },
                clientInfo = new { name = "workspace-selection-test", version = "1.0.0" }
            }, 120_000);

            var initializeResult = initialize.GetProperty("result");
            Assert.Contains("set_solution_root", initializeResult.GetProperty("instructions").GetString());

            await SendNotificationAsync(server, "notifications/initialized");

            var switched = await SendRequestAsync(server, 2, "tools/call", new
            {
                name = "set_solution_root",
                arguments = new { rootPath = _targetRoot, warmUp = false }
            }, 120_000);

            var switchBody = ParseToolBody(switched);
            Assert.True(switchBody.GetProperty("changed").GetBoolean());
            Assert.Equal(Path.GetFullPath(targetSolution), switchBody.GetProperty("solutionPath").GetString());
            Assert.False(switchBody.TryGetProperty("followEnabled", out _));

            var status = await SendRequestAsync(server, 3, "tools/call", new
            {
                name = "get_workspace_status",
                arguments = new { }
            }, 120_000);

            var statusBody = ParseToolBody(status);
            Assert.Equal(Path.GetFullPath(targetSolution), statusBody.GetProperty("solutionPath").GetString());

            var unchanged = await SendRequestAsync(server, 4, "tools/call", new
            {
                name = "set_solution_root",
                arguments = new { rootPath = _targetRoot, warmUp = false }
            }, 30_000);
            var unchangedBody = ParseToolBody(unchanged);
            Assert.False(unchangedBody.GetProperty("changed").GetBoolean());

            var emptyRoot = Path.Combine(_targetRoot, "empty");
            Directory.CreateDirectory(Path.Combine(emptyRoot, ".git"));
            var emptySelection = await SendRequestAsync(server, 5, "tools/call", new
            {
                name = "set_solution_root",
                arguments = new { rootPath = emptyRoot, warmUp = false }
            }, 120_000);
            var emptyResult = emptySelection.GetProperty("result");
            Assert.True(emptyResult.GetProperty("isError").GetBoolean());
            Assert.Contains(
                "No .sln or .slnx",
                emptyResult.GetProperty("content")[0].GetProperty("text").GetString());

            var statusAfterEmptyRoot = await SendRequestAsync(server, 6, "tools/call", new
            {
                name = "get_workspace_status",
                arguments = new { }
            }, 120_000);
            Assert.Equal(
                Path.GetFullPath(targetSolution),
                ParseToolBody(statusAfterEmptyRoot).GetProperty("solutionPath").GetString());

            var selectedByPath = await SendRequestAsync(server, 7, "tools/call", new
            {
                name = "set_solution_path",
                arguments = new
                {
                    solutionPath = Path.Combine(solutionDir, "RoslynMcp.sln"),
                    warmUp = false
                }
            }, 120_000);
            Assert.Equal(
                Path.GetFullPath(Path.Combine(solutionDir, "RoslynMcp.sln")),
                ParseToolBody(selectedByPath).GetProperty("solutionPath").GetString());

            var selectedByRootAgain = await SendRequestAsync(server, 8, "tools/call", new
            {
                name = "set_solution_root",
                arguments = new { rootPath = _targetRoot, warmUp = false }
            }, 120_000);
            var selectedAgainBody = ParseToolBody(selectedByRootAgain);
            Assert.True(selectedAgainBody.GetProperty("changed").GetBoolean());
            Assert.Equal(Path.GetFullPath(targetSolution), selectedAgainBody.GetProperty("solutionPath").GetString());

            for (var requestId = 9; requestId <= 10; requestId++)
            {
                var search = await SendRequestAsync(server, requestId, "tools/call", new
                {
                    name = "text_search",
                    arguments = new { pattern = "StickyTargetMarker", pageSize = 10, page = 0 }
                }, 120_000);
                var items = ParseToolBody(search).GetProperty("matches").GetProperty("items");
                Assert.NotEmpty(items.EnumerateArray());
                Assert.All(
                    items.EnumerateArray(),
                    item => Assert.StartsWith(
                        Path.GetFullPath(_targetRoot),
                        item.GetProperty("filePath").GetString(),
                        StringComparison.OrdinalIgnoreCase));
            }
        }
        finally
        {
            await StopServerAsync(server);
            var diagnostics = await stderr;
            Assert.DoesNotContain("terminated unexpectedly", diagnostics, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task SetSolutionRootAppliesTargetWatchFilesSetting()
    {
        var initialRoot = Path.Combine(_targetRoot, "watch-enabled");
        var targetRoot = Path.Combine(_targetRoot, "watch-disabled");
        var initialSolution = CreateTestSolution(
            initialRoot,
            "Initial",
            "WatchEnabledBeforeEditMarker");
        var targetSolution = CreateTestSolution(
            targetRoot,
            "Target",
            "WatchDisabledLoadedMarker");
        ConfigureWorkspace(initialRoot, watchFiles: true);
        ConfigureWorkspace(targetRoot, watchFiles: false);

        var solutionDir = FindSolutionDir();
        var serverExe = FindServerExe(solutionDir);
        Assert.True(File.Exists(serverExe), $"Server exe not found: {serverExe}. Run 'dotnet build' first.");

        using var server = StartServer(serverExe, solutionDir, initialSolution);
        var stderr = server.StandardError.ReadToEndAsync();

        try
        {
            await SendRequestAsync(server, 1, "initialize", new
            {
                protocolVersion = "2024-11-05",
                capabilities = new { },
                clientInfo = new { name = "workspace-config-test", version = "1.0.0" }
            }, 120_000);
            await SendNotificationAsync(server, "notifications/initialized");

            var requestId = 2;
            File.WriteAllText(
                Path.Combine(initialRoot, "Class1.cs"),
                "public sealed class WatchEnabledAfterEditMarker { }");

            var initialEditVisible = false;
            var watcherDeadline = DateTime.UtcNow.AddSeconds(15);
            while (DateTime.UtcNow < watcherDeadline)
            {
                if (await HasTextMatchAsync(server, requestId++, "WatchEnabledAfterEditMarker"))
                {
                    initialEditVisible = true;
                    break;
                }

                await Task.Delay(100);
            }
            Assert.True(initialEditVisible, "watcher positive control did not observe the initial edit");

            var switched = await SendRequestAsync(server, requestId++, "tools/call", new
            {
                name = "set_solution_root",
                arguments = new { rootPath = targetRoot, warmUp = false }
            }, 120_000);
            Assert.Equal(
                Path.GetFullPath(targetSolution),
                ParseToolBody(switched).GetProperty("solutionPath").GetString());
            Assert.True(await HasTextMatchAsync(server, requestId++, "WatchDisabledLoadedMarker"));

            File.WriteAllText(
                Path.Combine(targetRoot, "Class1.cs"),
                "public sealed class WatchDisabledAfterEditMarker { }");

            var targetEditVisible = false;
            var disabledDeadline = DateTime.UtcNow.AddSeconds(5);
            while (DateTime.UtcNow < disabledDeadline)
            {
                if (await HasTextMatchAsync(server, requestId++, "WatchDisabledAfterEditMarker"))
                {
                    targetEditVisible = true;
                    break;
                }

                await Task.Delay(100);
            }

            Assert.False(targetEditVisible, "target edit became visible after file watching was disabled");
            Assert.True(await HasTextMatchAsync(server, requestId, "WatchDisabledLoadedMarker"));
        }
        finally
        {
            await StopServerAsync(server);
            var diagnostics = await stderr;
            Assert.DoesNotContain("terminated unexpectedly", diagnostics, StringComparison.OrdinalIgnoreCase);
        }
    }

    public void Dispose()
    {
        try { Directory.Delete(_targetRoot, recursive: true); } catch { }
    }

    private static Process StartServer(
        string serverExe,
        string workingDirectory,
        string initialSolutionPath)
    {
        var server = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = serverExe,
                WorkingDirectory = workingDirectory,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };
        server.StartInfo.ArgumentList.Add("--solution-path");
        server.StartInfo.ArgumentList.Add(initialSolutionPath);
        server.StartInfo.EnvironmentVariables.Remove("ROSLYNMCP_SOLUTION_PATH");
        server.StartInfo.EnvironmentVariables.Remove("ROSLYNMCP_BOOTSTRAP_SOLUTION_PATH");
        server.Start();
        server.StandardInput.AutoFlush = true;
        return server;
    }

    private static async Task<JsonElement> SendRequestAsync(
        Process server,
        int id,
        string method,
        object? parameters,
        int timeoutMs)
    {
        await server.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id,
            method,
            @params = parameters
        }));

        using var timeout = new CancellationTokenSource(timeoutMs);
        while (true)
        {
            var line = await server.StandardOutput.ReadLineAsync(timeout.Token);
            Assert.False(string.IsNullOrWhiteSpace(line), $"Server output ended while waiting for response {id}");
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            if (root.TryGetProperty("id", out var responseId) && responseId.GetInt32() == id)
                return root.Clone();
        }
    }

    private static Task SendNotificationAsync(Process server, string method) =>
        server.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            method
        }));

    private static JsonElement ParseToolBody(JsonElement response)
    {
        var result = response.GetProperty("result");
        Assert.False(result.TryGetProperty("isError", out var isError) && isError.GetBoolean());
        var text = result.GetProperty("content")[0].GetProperty("text").GetString();
        Assert.False(string.IsNullOrWhiteSpace(text));
        using var body = JsonDocument.Parse(text);
        return body.RootElement.Clone();
    }

    private static async Task<bool> HasTextMatchAsync(Process server, int id, string pattern)
    {
        var search = await SendRequestAsync(server, id, "tools/call", new
        {
            name = "text_search",
            arguments = new { pattern, pageSize = 10, page = 0 }
        }, 120_000);
        return ParseToolBody(search)
            .GetProperty("matches")
            .GetProperty("items")
            .GetArrayLength() > 0;
    }

    private static string CreateTestSolution(string root, string name, string marker)
    {
        Directory.CreateDirectory(Path.Combine(root, ".git"));
        var solutionPath = Path.Combine(root, name + ".sln");
        File.WriteAllText(
            solutionPath,
            "Microsoft Visual Studio Solution File, Format Version 12.00\r\n" +
            "# Visual Studio Version 17\r\n" +
            $"Project(\"{{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}}\") = \"{name}\", \"{name}.csproj\", \"{{60483A71-E0B3-43E3-82AB-F69776481045}}\"\r\n" +
            "EndProject\r\nGlobal\r\nEndGlobal\r\n");
        File.WriteAllText(
            Path.Combine(root, name + ".csproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
        File.WriteAllText(
            Path.Combine(root, "Class1.cs"),
            $"public sealed class {marker} {{ }}");
        return solutionPath;
    }

    private static void ConfigureWorkspace(string root, bool watchFiles)
    {
        var config = new ConfigManager(Path.Combine(root, ".roslyn-mcp-data"));
        Assert.True(config.Set("workspace.watch_files", watchFiles.ToString(), out var watchError) != null, watchError);
        Assert.True(config.Set("workspace.idle_unload_minutes", "0", out var idleError) != null, idleError);
        Assert.True(config.Set("graph.auto_rebuild", "false", out var graphError) != null, graphError);
    }

    private static async Task StopServerAsync(Process server)
    {
        if (server.HasExited) return;

        server.StandardInput.Close();
        using var timeout = new CancellationTokenSource(5_000);
        try
        {
            await server.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            server.Kill(entireProcessTree: true);
            await server.WaitForExitAsync();
        }
    }

    private static string FindSolutionDir()
    {
        var directory = AppContext.BaseDirectory;
        while (directory != null)
        {
            if (File.Exists(Path.Combine(directory, "RoslynMcp.sln"))) return directory;
            directory = Path.GetDirectoryName(directory);
        }

        throw new InvalidOperationException("RoslynMcp.sln not found above " + AppContext.BaseDirectory);
    }

    private static string FindServerExe(string solutionDir)
    {
        var envPath = Environment.GetEnvironmentVariable("ROSLYNMCP_TEST_SERVER_PATH");
        if (!string.IsNullOrEmpty(envPath) && File.Exists(envPath)) return envPath;

        var exeName = OperatingSystem.IsWindows() ? "RoslynMcp.Server.exe" : "RoslynMcp.Server";
        var serverBinDir = Path.Combine(solutionDir, "src", "RoslynMcp.Server", "bin");

        foreach (var config in new[] { "Release", "Debug" })
        {
            var configDir = Path.Combine(serverBinDir, config);
            if (!Directory.Exists(configDir)) continue;

            foreach (var tfmDir in Directory.GetDirectories(configDir))
            {
                var candidate = Path.Combine(tfmDir, exeName);
                if (File.Exists(candidate)) return candidate;
            }
        }

        return Path.Combine(serverBinDir, "Release", exeName);
    }
}
