using System.Diagnostics;
using System.Text.Json;
using Xunit;

namespace RoslynMcp.Integration;

[Collection("workspace")]
public sealed class WorkspaceRootFollowTest : IDisposable
{
    private readonly string _targetRoot = Path.Combine(
        Path.GetTempPath(),
        "RoslynMcpTests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task SetSolutionRootSwitchesTheServerToTheDiscoveredSolution()
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
        File.WriteAllText(Path.Combine(_targetRoot, "Class1.cs"), "public sealed class Class1 { }");

        var solutionDir = FindSolutionDir();
        var serverExe = FindServerExe(solutionDir);
        Assert.True(File.Exists(serverExe), $"Server exe not found: {serverExe}. Run 'dotnet build' first.");

        using var server = StartServer(serverExe, solutionDir);
        var stderr = server.StandardError.ReadToEndAsync();

        try
        {
            var initialize = await SendRequestAsync(server, 1, "initialize", new
            {
                protocolVersion = "2024-11-05",
                capabilities = new { },
                clientInfo = new { name = "workspace-root-follow-test", version = "1.0.0" }
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
            Assert.True(unchangedBody.GetProperty("followEnabled").GetBoolean());

            var emptyRoot = Path.Combine(_targetRoot, "empty");
            Directory.CreateDirectory(Path.Combine(emptyRoot, ".git"));
            var failedFollow = await SendRequestAsync(server, 5, "tools/call", new
            {
                name = "set_solution_root",
                arguments = new { rootPath = emptyRoot, warmUp = false }
            }, 120_000);
            Assert.True(failedFollow.GetProperty("result").GetProperty("isError").GetBoolean());

            var statusAfterFailure = await SendRequestAsync(server, 6, "tools/call", new
            {
                name = "get_workspace_status",
                arguments = new { }
            }, 120_000);
            Assert.Equal(
                Path.GetFullPath(targetSolution),
                ParseToolBody(statusAfterFailure).GetProperty("solutionPath").GetString());

            var disabled = await SendRequestAsync(server, 7, "tools/call", new
            {
                name = "config_set",
                arguments = new { key = "workspace.follow_roots", value = "false" }
            }, 30_000);
            var disabledResult = disabled.GetProperty("result");
            Assert.False(disabledResult.TryGetProperty("isError", out var configError) && configError.GetBoolean());

            var disabledFollow = await SendRequestAsync(server, 8, "tools/call", new
            {
                name = "set_solution_root",
                arguments = new { rootPath = _targetRoot, warmUp = false }
            }, 30_000);
            var disabledBody = ParseToolBody(disabledFollow);
            Assert.False(disabledBody.GetProperty("changed").GetBoolean());
            Assert.False(disabledBody.GetProperty("followEnabled").GetBoolean());

            await SendRequestAsync(server, 9, "tools/call", new
            {
                name = "config_set",
                arguments = new { key = "workspace.follow_roots", value = "true" }
            }, 30_000);
            await SendRequestAsync(server, 10, "tools/call", new
            {
                name = "set_solution_path",
                arguments = new { solutionPath = targetSolution, warmUp = false }
            }, 120_000);

            var manuallyPinned = await SendRequestAsync(server, 11, "tools/call", new
            {
                name = "set_solution_root",
                arguments = new { rootPath = solutionDir, warmUp = false }
            }, 30_000);
            var manuallyPinnedBody = ParseToolBody(manuallyPinned);
            Assert.False(manuallyPinnedBody.GetProperty("changed").GetBoolean());
            Assert.False(manuallyPinnedBody.GetProperty("followEnabled").GetBoolean());
            Assert.Equal(Path.GetFullPath(targetSolution), manuallyPinnedBody.GetProperty("solutionPath").GetString());
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

    private static Process StartServer(string serverExe, string workingDirectory)
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
        server.StartInfo.EnvironmentVariables.Remove("ROSLYNMCP_SOLUTION_PATH");
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
