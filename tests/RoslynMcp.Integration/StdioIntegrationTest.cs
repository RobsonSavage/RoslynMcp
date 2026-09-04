using System.Diagnostics;
using System.Text.Json;
using Xunit;
using Xunit.Abstractions;

namespace RoslynMcp.Integration;

/// <summary>
/// End-to-end stdio integration test. Starts the RoslynMcp.Server process,
/// sends MCP JSON-RPC messages over stdin, validates responses from stdout.
/// Uses RoslynMcp.sln itself as the test solution.
/// </summary>
public sealed class ServerFixture : IAsyncLifetime
{
    private Process? _server;
    private StreamWriter? _stdin;
    private StreamReader? _stdout;
    private int _nextId;
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private Task? _stderrDrainTask;

    /// <summary>True after successful MCP initialize handshake.</summary>
    public bool IsReady { get; private set; }

    /// <summary>Reason for failure if IsReady is false.</summary>
    public string? FailureReason { get; private set; }

    /// <summary>Number of tools reported by tools/list.</summary>
    public int ToolCount { get; private set; }

    public string? SolutionPath { get; private set; }

    public async Task InitializeAsync()
    {
        try
        {
            var solutionDir = FindSolutionDir();
            var serverExe = FindServerExe(solutionDir);
            var solutionPath = Path.Combine(solutionDir, "RoslynMcp.sln");
            SolutionPath = solutionPath;

            if (!File.Exists(serverExe))
            {
                FailureReason = $"Server exe not found: {serverExe}. Run 'dotnet build' first.";
                return;
            }

            if (!File.Exists(solutionPath))
            {
                FailureReason = $"Solution not found: {solutionPath}";
                return;
            }

            _server = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = serverExe,
                    Arguments = $"--solution-path \"{solutionPath}\"",
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };

            _server.Start();
            _stdin = _server.StandardInput;
            _stdin.AutoFlush = true;
            _stdout = _server.StandardOutput;

            // Drain stderr asynchronously to prevent buffer deadlock
            _stderrDrainTask = Task.Run(async () =>
            {
                try { while (await _server.StandardError.ReadLineAsync() != null) { } }
                catch { }
            });

            // MCP initialize handshake (generous timeout - solution load may take a while)
            var initResult = await SendRequestCoreAsync("initialize", new
            {
                protocolVersion = "2024-11-05",
                capabilities = new { },
                clientInfo = new { name = "integration-test", version = "1.0.0" }
            }, timeoutMs: 120_000);

            if (initResult == null)
            {
                FailureReason = "initialize request timed out (120s)";
                return;
            }

            // Send initialized notification
            await SendNotificationAsync("notifications/initialized");

            IsReady = true;
        }
        catch (Exception ex)
        {
            FailureReason = $"Fixture init failed: {ex.Message}";
        }
    }

    public async Task DisposeAsync()
    {
        if (_server != null && !_server.HasExited)
        {
            try { _stdin?.Close(); }
            catch { }

            if (!_server.WaitForExit(5000))
            {
                try { _server.Kill(entireProcessTree: true); }
                catch { }
            }

            _server.Dispose();
        }

        if (_stderrDrainTask != null)
            await _stderrDrainTask;

        _sendLock.Dispose();
    }

    /// <summary>
    /// Send a JSON-RPC request and wait for the matching response.
    /// Thread-safe via semaphore.
    /// </summary>
    public async Task<JsonElement?> SendRequestAsync(string method, object? @params = null, int timeoutMs = 30_000)
    {
        await _sendLock.WaitAsync();
        try
        {
            return await SendRequestCoreAsync(method, @params, timeoutMs);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    private async Task<JsonElement?> SendRequestCoreAsync(string method, object? @params, int timeoutMs)
    {
        if (_stdin == null || _stdout == null) return null;

        var id = Interlocked.Increment(ref _nextId);
        var request = JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            id,
            method,
            @params
        });

        await _stdin.WriteLineAsync(request);

        using var cts = new CancellationTokenSource(timeoutMs);

        while (!cts.IsCancellationRequested)
        {
            var line = await ReadLineAsync(cts.Token);
            if (line == null) return null; // stream closed

            if (string.IsNullOrWhiteSpace(line)) continue;

            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;

                // Skip notifications (no "id" property)
                if (!root.TryGetProperty("id", out var idProp)) continue;

                if (idProp.GetInt32() == id)
                {
                    if (root.TryGetProperty("result", out var result))
                        return result.Clone();

                    if (root.TryGetProperty("error", out var error))
                        return error.Clone();

                    return null;
                }
            }
            catch (JsonException)
            {
                // Skip malformed lines (could be stderr leakage)
            }
        }

        return null; // timeout
    }

    private async Task SendNotificationAsync(string method, object? @params = null)
    {
        if (_stdin == null) return;

        var notification = JsonSerializer.Serialize(new
        {
            jsonrpc = "2.0",
            method,
            @params
        });

        await _stdin.WriteLineAsync(notification);
    }

    private async Task<string?> ReadLineAsync(CancellationToken ct)
    {
        if (_stdout == null) return null;

        try
        {
            var readTask = _stdout.ReadLineAsync(ct);
            return await readTask;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (ObjectDisposedException)
        {
            return null;
        }
    }

    private static string FindSolutionDir()
    {
        // Walk up from assembly base directory looking for RoslynMcp.sln
        var dir = AppContext.BaseDirectory;
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir, "RoslynMcp.sln")))
                return dir;
            dir = Path.GetDirectoryName(dir);
        }

        // Fallback: walk up from current directory
        dir = Directory.GetCurrentDirectory();
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir, "RoslynMcp.sln")))
                return dir;
            dir = Path.GetDirectoryName(dir);
        }

        throw new InvalidOperationException("Could not find RoslynMcp.sln");
    }

    private static string FindServerExe(string solutionDir)
    {
        // Allow override via environment variable
        var envPath = Environment.GetEnvironmentVariable("ROSLYNMCP_TEST_SERVER_PATH");
        if (!string.IsNullOrEmpty(envPath) && File.Exists(envPath))
            return envPath;

        var exeName = OperatingSystem.IsWindows() ? "RoslynMcp.Server.exe" : "RoslynMcp.Server";
        var serverBinDir = Path.Combine(solutionDir, "src", "RoslynMcp.Server", "bin");

        if (!Directory.Exists(serverBinDir))
            return Path.Combine(solutionDir, "src", "RoslynMcp.Server", "bin", "Debug", exeName);

        // Search for the exe in any configuration/framework subfolder
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

        // Fallback
        return Path.Combine(serverBinDir, "Debug", exeName);
    }
}

public class StdioIntegrationTest : IClassFixture<ServerFixture>
{
    private readonly ServerFixture _server;
    private readonly ITestOutputHelper _output;

    public StdioIntegrationTest(ServerFixture server, ITestOutputHelper output)
    {
        _server = server;
        _output = output;
    }

    private void EnsureReady()
    {
        if (!_server.IsReady)
        {
            Assert.Fail($"Server fixture not available: {_server.FailureReason}");
        }
    }

    [Fact]
    public void Server_Initializes()
    {
        if (!_server.IsReady)
            Assert.Fail($"Server failed to initialize: {_server.FailureReason}");
    }

    [Fact]
    public async Task ToolsList_Returns95Tools()
    {
        EnsureReady();

        var result = await _server.SendRequestAsync("tools/list", new { });
        Assert.NotNull(result);

        var tools = result.Value.GetProperty("tools");
        var count = tools.GetArrayLength();
        _output.WriteLine($"tools/list returned {count} tools");

        Assert.True(count >= 90, $"Expected at least 90 tools but found {count}");

        // Verify some expected tool names exist
        var toolNames = new HashSet<string>();
        foreach (var tool in tools.EnumerateArray())
            toolNames.Add(tool.GetProperty("name").GetString()!);

        Assert.Contains("find_references", toolNames);
        Assert.Contains("get_workspace_status", toolNames);
        Assert.Contains("set_solution_root", toolNames);
        Assert.Contains("understand_type", toolNames);
        Assert.Contains("session_start", toolNames);
        Assert.Contains("graph_add_node", toolNames);
        Assert.Contains("kb_search", toolNames);
        Assert.Contains("apollo_diagnose", toolNames);
        Assert.Contains("preview_rename", toolNames);
    }

    [Fact]
    public async Task GetWorkspaceStatus_ReturnsProjectInfo()
    {
        EnsureReady();

        var result = await _server.SendRequestAsync("tools/call", new
        {
            name = "get_workspace_status",
            arguments = new { }
        });

        Assert.NotNull(result);

        var content = result.Value.GetProperty("content");
        var text = content[0].GetProperty("text").GetString()!;
        _output.WriteLine($"get_workspace_status: {text}");

        // Response should contain project count info as JSON
        using var parsed = JsonDocument.Parse(text);
        var root = parsed.RootElement;
        Assert.True(root.TryGetProperty("projectCount", out var projectCount));
        Assert.True(projectCount.GetInt32() >= 4); // RoslynMcp.sln has 6 projects
    }

    [Fact]
    public async Task GetSolutionStructure_ReturnsProjects()
    {
        EnsureReady();

        var result = await _server.SendRequestAsync("tools/call", new
        {
            name = "get_solution_structure",
            arguments = new { includeMetadata = false }
        });

        Assert.NotNull(result);

        var text = result.Value.GetProperty("content")[0].GetProperty("text").GetString()!;
        _output.WriteLine($"get_solution_structure: {text[..Math.Min(500, text.Length)]}");

        using var parsed = JsonDocument.Parse(text);
        var projects = parsed.RootElement.GetProperty("projects");
        Assert.True(projects.GetArrayLength() >= 4);

        // Verify known project names
        var names = new HashSet<string>();
        foreach (var p in projects.EnumerateArray())
            names.Add(p.GetProperty("name").GetString()!);

        Assert.Contains("RoslynMcp.Shared", names);
        Assert.Contains("RoslynMcp.Core", names);
        Assert.Contains("RoslynMcp.Server", names);
    }

    [Fact]
    public async Task TextSearch_FindsPattern()
    {
        EnsureReady();

        var result = await _server.SendRequestAsync("tools/call", new
        {
            name = "text_search",
            arguments = new
            {
                pattern = "IWorkspaceProvider",
                pageSize = 5,
                page = 0
            }
        });

        Assert.NotNull(result);

        var text = result.Value.GetProperty("content")[0].GetProperty("text").GetString()!;
        _output.WriteLine($"text_search: {text[..Math.Min(500, text.Length)]}");

        using var parsed = JsonDocument.Parse(text);
        var matches = parsed.RootElement.GetProperty("matches");
        Assert.True(matches.GetProperty("totalCount").GetInt32() > 0);
    }

    [Fact]
    public async Task ConfigList_ReturnsEntries()
    {
        EnsureReady();

        var result = await _server.SendRequestAsync("tools/call", new
        {
            name = "config_list",
            arguments = new { }
        });

        Assert.NotNull(result);

        var text = result.Value.GetProperty("content")[0].GetProperty("text").GetString()!;
        _output.WriteLine($"config_list: {text[..Math.Min(500, text.Length)]}");

        using var parsed = JsonDocument.Parse(text);
        var entries = parsed.RootElement.GetProperty("entries");
        Assert.True(entries.GetArrayLength() > 0);
    }

    [Fact]
    public async Task MemoryStats_ReturnsStats()
    {
        EnsureReady();

        var result = await _server.SendRequestAsync("tools/call", new
        {
            name = "memory_stats",
            arguments = new { }
        });

        Assert.NotNull(result);

        var text = result.Value.GetProperty("content")[0].GetProperty("text").GetString()!;
        _output.WriteLine($"memory_stats: {text}");

        using var parsed = JsonDocument.Parse(text);
        Assert.True(parsed.RootElement.TryGetProperty("totalEntries", out _));
    }

    [Fact]
    public async Task SetSolutionPath_SwitchesTheCompleteRuntimeContext()
    {
        EnsureReady();
        Assert.NotNull(_server.SolutionPath);

        var result = await _server.SendRequestAsync("tools/call", new
        {
            name = "set_solution_path",
            arguments = new
            {
                solutionPath = _server.SolutionPath,
                warmUp = false
            }
        }, timeoutMs: 120_000);

        Assert.NotNull(result);
        var text = result.Value.GetProperty("content")[0].GetProperty("text").GetString()!;
        using var parsed = JsonDocument.Parse(text);
        Assert.Equal(
            Path.GetFullPath(_server.SolutionPath),
            parsed.RootElement.GetProperty("solutionPath").GetString());

        var stats = await _server.SendRequestAsync("tools/call", new
        {
            name = "memory_stats",
            arguments = new { }
        });
        Assert.NotNull(stats);
        Assert.False(stats.Value.TryGetProperty("isError", out var isError) && isError.GetBoolean());
    }

    [Fact]
    public async Task SetSolutionRoot_DoesNotOverrideAnExplicitStartupPin()
    {
        EnsureReady();
        Assert.NotNull(_server.SolutionPath);

        var rootPath = Path.GetDirectoryName(_server.SolutionPath)!;
        var result = await _server.SendRequestAsync("tools/call", new
        {
            name = "set_solution_root",
            arguments = new { rootPath, warmUp = false }
        });

        Assert.NotNull(result);
        Assert.True(result.Value.TryGetProperty("content", out var content), result.Value.ToString());
        var text = content[0].GetProperty("text").GetString()!;
        using var parsed = JsonDocument.Parse(text);
        Assert.False(parsed.RootElement.GetProperty("changed").GetBoolean());
        Assert.False(parsed.RootElement.GetProperty("followEnabled").GetBoolean());
        Assert.Equal(Path.GetFullPath(_server.SolutionPath), parsed.RootElement.GetProperty("solutionPath").GetString());
        Assert.Contains("disabled", parsed.RootElement.GetProperty("message").GetString());
    }

    [Fact]
    public async Task SetSolutionRoot_ReturnsValidNoOpJsonForPreToolUseHook()
    {
        EnsureReady();
        Assert.NotNull(_server.SolutionPath);

        var rootPath = Path.GetDirectoryName(_server.SolutionPath)!;
        var result = await _server.SendRequestAsync("tools/call", new
        {
            name = "set_solution_root",
            arguments = new { rootPath, warmUp = false, hookOutput = true }
        });

        Assert.NotNull(result);
        Assert.True(result.Value.TryGetProperty("content", out var content), result.Value.ToString());
        Assert.Equal("{}", content[0].GetProperty("text").GetString());
    }

    [Fact]
    public void WorkspaceFollowPlugin_RequestsPreToolUseHookOutput()
    {
        Assert.NotNull(_server.SolutionPath);

        var solutionDir = Path.GetDirectoryName(_server.SolutionPath)!;
        var hooksPath = Path.Combine(solutionDir, "plugins", "roslyn-workspace-follow", "hooks", "hooks.json");
        using var hooks = JsonDocument.Parse(File.ReadAllText(hooksPath));
        var input = hooks.RootElement
            .GetProperty("hooks")
            .GetProperty("PreToolUse")[0]
            .GetProperty("hooks")[0]
            .GetProperty("input");

        Assert.True(input.GetProperty("hookOutput").GetBoolean());
    }
}
