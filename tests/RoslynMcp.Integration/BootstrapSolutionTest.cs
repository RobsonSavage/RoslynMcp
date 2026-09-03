using System.Diagnostics;
using System.Text.Json;
using Xunit;

namespace RoslynMcp.Integration;

/// <summary>
/// Covers ROSLYNMCP_BOOTSTRAP_SOLUTION_PATH: the fallback that lets the server start in a tree
/// holding no solution at all.
///
/// Without it, discovery fails and Program.cs returns 2 before the MCP host is built, so
/// set_solution_root - the very tool that would move the server to the right solution - does not
/// exist yet to be called. A client started in a Python repository or a notes directory therefore
/// got a dead connection for the whole session.
///
/// The load-bearing assertion is the last one: a bootstrap is not a pin. An explicit
/// --solution-path / ROSLYNMCP_SOLUTION_PATH sets hasExplicitSolutionPin and switches root
/// following off for the process, which would trade a server that never starts for a server that
/// starts and never follows.
/// </summary>
[Collection("workspace")]
public sealed class BootstrapSolutionTest : IDisposable
{
    private readonly string _sandbox = Path.Combine(
        Path.GetTempPath(),
        "RoslynMcpTests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task BootstrapSolutionStartsTheServerAndLeavesRootFollowingOn()
    {
        // A git repository with no solution in it: exactly what discovery cannot answer.
        var emptyRepo = Path.Combine(_sandbox, "no-solution");
        Directory.CreateDirectory(Path.Combine(emptyRepo, ".git"));

        var bootstrapSolution = CreateSolution(Path.Combine(_sandbox, "bootstrap"), "Bootstrap");
        var targetSolution = CreateSolution(Path.Combine(_sandbox, "target"), "Target");
        var targetRoot = Path.GetDirectoryName(targetSolution)!;

        var serverExe = FindServerExe(FindSolutionDir());
        Assert.True(File.Exists(serverExe), $"Server exe not found: {serverExe}. Run 'dotnet build' first.");

        using var server = StartServer(serverExe, emptyRepo, bootstrapSolution);
        var stderr = server.StandardError.ReadToEndAsync();

        try
        {
            // The handshake completing at all is the finding: without the fallback the process has
            // already exited 2 and this read ends the stream.
            var initialize = await SendRequestAsync(server, 1, "initialize", new
            {
                protocolVersion = "2024-11-05",
                capabilities = new { },
                clientInfo = new { name = "bootstrap-solution-test", version = "1.0.0" }
            }, 120_000);
            Assert.True(initialize.TryGetProperty("result", out _));

            await SendNotificationAsync(server, "notifications/initialized");

            var status = await SendRequestAsync(server, 2, "tools/call", new
            {
                name = "get_workspace_status",
                arguments = new { }
            }, 120_000);
            Assert.Equal(
                Path.GetFullPath(bootstrapSolution),
                ParseToolBody(status).GetProperty("solutionPath").GetString());

            // Not a pin. A client reporting its root moves the server off the bootstrap solution,
            // which is the whole difference between this variable and ROSLYNMCP_SOLUTION_PATH.
            var followed = await SendRequestAsync(server, 3, "tools/call", new
            {
                name = "set_solution_root",
                arguments = new { rootPath = targetRoot, warmUp = false }
            }, 120_000);
            var followedBody = ParseToolBody(followed);
            Assert.True(followedBody.GetProperty("changed").GetBoolean());
            Assert.True(followedBody.GetProperty("followEnabled").GetBoolean());
            Assert.Equal(Path.GetFullPath(targetSolution), followedBody.GetProperty("solutionPath").GetString());
        }
        finally
        {
            await StopServerAsync(server);
            var diagnostics = await stderr;
            Assert.DoesNotContain("terminated unexpectedly", diagnostics, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task AMissingBootstrapSolutionIsIgnoredRatherThanLoaded()
    {
        // Handing a nonexistent path to MSBuildWorkspace throws several frames later, in a message
        // that names MSBuild rather than the misconfigured variable. Falling through leaves the
        // "no solution resolved" error, which is the one a reader can act on.
        var emptyRepo = Path.Combine(_sandbox, "no-solution");
        Directory.CreateDirectory(Path.Combine(emptyRepo, ".git"));

        var serverExe = FindServerExe(FindSolutionDir());
        Assert.True(File.Exists(serverExe), $"Server exe not found: {serverExe}. Run 'dotnet build' first.");

        using var server = StartServer(serverExe, emptyRepo, Path.Combine(_sandbox, "absent", "Nope.sln"));
        var stderr = server.StandardError.ReadToEndAsync();

        using var timeout = new CancellationTokenSource(60_000);
        await server.WaitForExitAsync(timeout.Token);

        Assert.Equal(2, server.ExitCode);
        Assert.Contains("No solution resolved from CWD", await stderr, StringComparison.Ordinal);
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

    private static Process StartServer(string serverExe, string workingDirectory, string bootstrapSolution)
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
        // A stale User-scope pin would both decide the solution and switch following off, which is
        // the state this test exists to tell apart from the bootstrap one.
        server.StartInfo.EnvironmentVariables.Remove("ROSLYNMCP_SOLUTION_PATH");
        server.StartInfo.EnvironmentVariables["ROSLYNMCP_BOOTSTRAP_SOLUTION_PATH"] = bootstrapSolution;
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
