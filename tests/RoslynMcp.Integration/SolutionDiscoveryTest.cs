using System.Diagnostics;
using System.Text.Json;
using Xunit;

namespace RoslynMcp.Integration;

/// <summary>
/// Covers CWD-based solution discovery. Every other stdio test passes
/// --solution-path explicitly, so nothing exercised the discovery walk itself -
/// which is how a filter that skipped RoslynMcp.sln by name survived: started
/// inside its own repo the server found nothing and exited before the handshake.
/// </summary>
[Collection("workspace")]
public sealed class SolutionDiscoveryTest
{
    [Fact]
    public async Task DiscoversSolutionFromWorkingDirectory()
    {
        var solutionDir = FindSolutionDir();
        var serverExe = FindServerExe(solutionDir);
        Assert.True(File.Exists(serverExe), $"Server exe not found: {serverExe}. Run 'dotnet build' first.");

        using var server = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = serverExe,
                // No --solution-path: the point is that discovery finds it.
                WorkingDirectory = solutionDir,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };

        // A stale User-scope variable would otherwise decide this for us.
        server.StartInfo.EnvironmentVariables.Remove("ROSLYNMCP_SOLUTION_PATH");

        server.Start();

        server.StandardInput.AutoFlush = true;
        var stderr = server.StandardError.ReadToEndAsync();

        string? response = null;
        try
        {
            await server.StandardInput.WriteLineAsync(JsonSerializer.Serialize(new
            {
                jsonrpc = "2.0",
                id = 1,
                method = "initialize",
                @params = new
                {
                    protocolVersion = "2024-11-05",
                    capabilities = new { },
                    clientInfo = new { name = "solution-discovery-test", version = "1.0.0" }
                }
            }));

            using var timeout = new CancellationTokenSource(120_000);
            response = await server.StandardOutput.ReadLineAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (!server.HasExited)
            {
                try { server.Kill(entireProcessTree: true); } catch { }
            }

            await server.WaitForExitAsync();
        }

        var diagnostics = await stderr;
        Assert.False(
            string.IsNullOrWhiteSpace(response),
            $"Server did not initialize with the discovered solution. Exit code {server.ExitCode}, stderr: {diagnostics}");

        using var document = JsonDocument.Parse(response);
        Assert.True(
            document.RootElement.TryGetProperty("result", out _),
            $"Initialize returned an error. Response: {response}, stderr: {diagnostics}");
    }

    private static string FindSolutionDir()
    {
        var dir = AppContext.BaseDirectory;
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir, "RoslynMcp.sln"))) return dir;
            dir = Path.GetDirectoryName(dir);
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
