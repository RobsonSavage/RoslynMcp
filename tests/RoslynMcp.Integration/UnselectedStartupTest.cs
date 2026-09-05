using System.Diagnostics;
using System.Text.Json;
using Xunit;

namespace RoslynMcp.Integration;

/// <summary>
/// Covers startup without a selected solution. The MCP host and selector tools must remain
/// available, while every solution-scoped tool fails with the same actionable error.
/// </summary>
[Collection("workspace")]
public sealed class UnselectedStartupTest : IDisposable
{
    private readonly string _sandbox = Path.Combine(
        Path.GetTempPath(),
        "RoslynMcpTests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task ServerStartsUnselectedAndCanSelectASolutionLater()
    {
        // A git repository with no solution in it: exactly what discovery cannot answer.
        var emptyRepo = Path.Combine(_sandbox, "no-solution");
        Directory.CreateDirectory(Path.Combine(emptyRepo, ".git"));

        var targetSolution = CreateSolution(Path.Combine(_sandbox, "target"), "Target");
        var targetRoot = Path.GetDirectoryName(targetSolution)!;

        var serverExe = FindServerExe(FindSolutionDir());
        Assert.True(File.Exists(serverExe), $"Server exe not found: {serverExe}. Run 'dotnet build' first.");

        using var server = StartServer(serverExe, emptyRepo);
        var stderr = server.StandardError.ReadToEndAsync();

        try
        {
            var initialize = await SendRequestAsync(server, 1, "initialize", new
            {
                protocolVersion = "2024-11-05",
                capabilities = new { },
                clientInfo = new { name = "unselected-startup-test", version = "1.0.0" }
            }, 120_000);
            Assert.True(initialize.TryGetProperty("result", out _));

            await SendNotificationAsync(server, "notifications/initialized");

            var status = await SendRequestAsync(server, 2, "tools/call", new
            {
                name = "get_workspace_status",
                arguments = new { }
            }, 120_000);
            var statusBody = ParseToolBody(status);
            Assert.Equal(JsonValueKind.Null, statusBody.GetProperty("solutionPath").ValueKind);
            Assert.Equal(0, statusBody.GetProperty("projectCount").GetInt32());
            Assert.False(statusBody.GetProperty("isSolutionSelected").GetBoolean());
            Assert.False(statusBody.GetProperty("isFullyLoaded").GetBoolean());
            Assert.False(Directory.Exists(Path.Combine(emptyRepo, ".roslyn-mcp-data")));

            foreach (var toolName in new[] { "text_search", "config_list" })
            {
                var blocked = await SendRequestAsync(server, toolName == "text_search" ? 3 : 4, "tools/call", new
                {
                    name = toolName,
                    arguments = toolName == "text_search"
                        ? (object)new { pattern = "anything", pageSize = 5, page = 0 }
                        : new { }
                }, 30_000);
                var blockedResult = blocked.GetProperty("result");
                Assert.True(blockedResult.GetProperty("isError").GetBoolean());
                Assert.Contains(
                    "NO_SOLUTION_SELECTED",
                    blockedResult.GetProperty("content")[0].GetProperty("text").GetString());
            }

            var selected = await SendRequestAsync(server, 5, "tools/call", new
            {
                name = "set_solution_root",
                arguments = new { rootPath = targetRoot, warmUp = false }
            }, 120_000);
            var selectedBody = ParseToolBody(selected);
            Assert.True(selectedBody.GetProperty("changed").GetBoolean());
            Assert.Equal(Path.GetFullPath(targetSolution), selectedBody.GetProperty("solutionPath").GetString());

            var selectedStatus = await SendRequestAsync(server, 6, "tools/call", new
            {
                name = "get_workspace_status",
                arguments = new { }
            }, 120_000);
            Assert.Equal(
                Path.GetFullPath(targetSolution),
                ParseToolBody(selectedStatus).GetProperty("solutionPath").GetString());
        }
        finally
        {
            await StopServerAsync(server);
            var diagnostics = await stderr;
            Assert.DoesNotContain("terminated unexpectedly", diagnostics, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task ObsoleteBootstrapVariableDoesNotSelectASolution()
    {
        var emptyRepo = Path.Combine(_sandbox, "no-solution");
        Directory.CreateDirectory(Path.Combine(emptyRepo, ".git"));
        var obsoleteBootstrap = CreateSolution(Path.Combine(_sandbox, "obsolete-bootstrap"), "Obsolete");

        var serverExe = FindServerExe(FindSolutionDir());
        Assert.True(File.Exists(serverExe), $"Server exe not found: {serverExe}. Run 'dotnet build' first.");

        using var server = StartServer(serverExe, emptyRepo, obsoleteBootstrap);
        var stderr = server.StandardError.ReadToEndAsync();

        try
        {
            await SendRequestAsync(server, 1, "initialize", new
            {
                protocolVersion = "2024-11-05",
                capabilities = new { },
                clientInfo = new { name = "obsolete-bootstrap-test", version = "1.0.0" }
            }, 120_000);
            await SendNotificationAsync(server, "notifications/initialized");

            var status = await SendRequestAsync(server, 2, "tools/call", new
            {
                name = "get_workspace_status",
                arguments = new { }
            }, 30_000);
            Assert.Equal(
                JsonValueKind.Null,
                ParseToolBody(status).GetProperty("solutionPath").ValueKind);
        }
        finally
        {
            await StopServerAsync(server);
            Assert.DoesNotContain("terminated unexpectedly", await stderr, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task InvalidExplicitStartupPathFallsBackToUnselected()
    {
        var emptyRepo = Path.Combine(_sandbox, "invalid-initial");
        Directory.CreateDirectory(Path.Combine(emptyRepo, ".git"));
        var missingSolution = Path.Combine(_sandbox, "missing", "Missing.sln");
        var serverExe = FindServerExe(FindSolutionDir());

        using var server = StartServer(
            serverExe,
            emptyRepo,
            initialSolutionPath: missingSolution);
        var stderr = server.StandardError.ReadToEndAsync();
        try
        {
            await SendRequestAsync(server, 1, "initialize", new
            {
                protocolVersion = "2024-11-05",
                capabilities = new { },
                clientInfo = new { name = "invalid-initial-test", version = "1.0.0" }
            }, 120_000);
            await SendNotificationAsync(server, "notifications/initialized");

            var status = await SendRequestAsync(server, 2, "tools/call", new
            {
                name = "get_workspace_status",
                arguments = new { }
            }, 30_000);
            Assert.Equal(
                JsonValueKind.Null,
                ParseToolBody(status).GetProperty("solutionPath").ValueKind);
        }
        finally
        {
            await StopServerAsync(server);
            var diagnostics = await stderr;
            Assert.Contains("starting without a selected solution", diagnostics, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("terminated unexpectedly", diagnostics, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task InvalidInitialDataDirectoryFallsBackToUnselected()
    {
        var initialSolution = CreateSolution(Path.Combine(_sandbox, "invalid-data"), "InvalidData");
        File.WriteAllText(
            Path.Combine(Path.GetDirectoryName(initialSolution)!, ".roslyn-mcp-data"),
            "blocks the data directory");
        var targetSolution = CreateSolution(Path.Combine(_sandbox, "valid-target"), "ValidTarget");
        var serverExe = FindServerExe(FindSolutionDir());

        using var server = StartServer(
            serverExe,
            Path.GetDirectoryName(initialSolution)!);
        var stderr = server.StandardError.ReadToEndAsync();
        try
        {
            await SendRequestAsync(server, 1, "initialize", new
            {
                protocolVersion = "2024-11-05",
                capabilities = new { },
                clientInfo = new { name = "invalid-data-test", version = "1.0.0" }
            }, 120_000);
            await SendNotificationAsync(server, "notifications/initialized");

            var status = await SendRequestAsync(server, 2, "tools/call", new
            {
                name = "get_workspace_status",
                arguments = new { }
            }, 30_000);
            Assert.Equal(
                JsonValueKind.Null,
                ParseToolBody(status).GetProperty("solutionPath").ValueKind);

            var selected = await SendRequestAsync(server, 3, "tools/call", new
            {
                name = "set_solution_root",
                arguments = new
                {
                    rootPath = Path.GetDirectoryName(targetSolution)!,
                    warmUp = false
                }
            }, 120_000);
            Assert.Equal(
                Path.GetFullPath(targetSolution),
                ParseToolBody(selected).GetProperty("solutionPath").GetString());
        }
        finally
        {
            await StopServerAsync(server);
            var diagnostics = await stderr;
            Assert.Contains(
                "solution data context could not be initialized; starting without a selected solution",
                diagnostics,
                StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("terminated unexpectedly", diagnostics, StringComparison.OrdinalIgnoreCase);
        }
    }

    public void Dispose()
    {
        try { Directory.Delete(_sandbox, recursive: true); } catch { }
    }

    /// <summary>A minimal loadable solution, so neither case pays for a real one.</summary>
    private static string CreateSolution(string root, string name)
    {
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(Path.Combine(root, ".git"));
        var solutionPath = Path.Combine(root, $"{name}.sln");
        File.WriteAllText(
            solutionPath,
            "Microsoft Visual Studio Solution File, Format Version 12.00\r\n" +
            "# Visual Studio Version 17\r\n" +
            $"Project(\"{{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}}\") = \"{name}\", \"{name}.csproj\", \"{{60483A71-E0B3-43E3-82AB-F69776481045}}\"\r\n" +
            "EndProject\r\nGlobal\r\nEndGlobal\r\n");
        File.WriteAllText(
            Path.Combine(root, $"{name}.csproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
        File.WriteAllText(Path.Combine(root, "Class1.cs"), "public sealed class Class1 { }");
        return solutionPath;
    }

    private static Process StartServer(
        string serverExe,
        string workingDirectory,
        string? obsoleteBootstrapSolution = null,
        string? initialSolutionPath = null)
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
        if (initialSolutionPath == null)
            server.StartInfo.EnvironmentVariables.Remove("ROSLYNMCP_SOLUTION_PATH");
        else
            server.StartInfo.EnvironmentVariables["ROSLYNMCP_SOLUTION_PATH"] = initialSolutionPath;
        if (obsoleteBootstrapSolution == null)
            server.StartInfo.EnvironmentVariables.Remove("ROSLYNMCP_BOOTSTRAP_SOLUTION_PATH");
        else
            server.StartInfo.EnvironmentVariables["ROSLYNMCP_BOOTSTRAP_SOLUTION_PATH"] = obsoleteBootstrapSolution;
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
