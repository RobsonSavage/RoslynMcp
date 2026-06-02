using RoslynMcp.Core.Helpers;
using RoslynMcp.Core.Services;
using RoslynMcp.Core.Tests.Helpers;
using RoslynMcp.Shared.Contracts.Util;
using Serilog;
using Xunit;

namespace RoslynMcp.Core.Tests.Services;

public class UtilServiceTests_Config : IDisposable
{
    private readonly ILogger _logger = new LoggerConfiguration().CreateLogger();
    private readonly string _configDir;
    private WorkspaceTestHelper? _helper;

    public UtilServiceTests_Config()
    {
        _configDir = Path.Combine(Path.GetTempPath(), "RoslynMcpTests", Guid.NewGuid().ToString());
        Directory.CreateDirectory(_configDir);
    }

    private UtilService CreateService(WorkspaceTestHelper helper)
    {
        _helper = helper;
        var provider = helper.CreateProvider();
        var helpers = helper.CreateHelpers(_logger);
        var config = new ConfigManager(_configDir);
        return new UtilService(provider, helpers, config, _logger);
    }

    private WorkspaceTestHelper CreateMinimalWorkspace()
    {
        return new WorkspaceTestHelper()
            .AddProject("TestProject")
            .AddDocument("TestProject", "Dummy.cs", "namespace TestNs { }");
    }

    public void Dispose()
    {
        _helper?.Dispose();
        try { Directory.Delete(_configDir, true); } catch { }
    }

    // ────────────────────────────────────────────────────────────────────
    // 1. ConfigGet_KnownKey_ReturnsDefinition
    // ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ConfigGet_KnownKey_ReturnsDefinition()
    {
        var svc = CreateService(CreateMinimalWorkspace());

        var result = await svc.ConfigGetAsync(new ConfigGetRequest("timeout.default"), CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error?.Message);
        var response = result.Value!;
        Assert.Equal("timeout.default", response.Key);
        Assert.Equal("30", response.DefaultValue);
        Assert.Equal("int", response.Type);
    }

    // ────────────────────────────────────────────────────────────────────
    // 2. ConfigGet_UnknownKey_ReturnsUnknownType
    // ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ConfigGet_UnknownKey_ReturnsUnknownType()
    {
        var svc = CreateService(CreateMinimalWorkspace());

        var result = await svc.ConfigGetAsync(new ConfigGetRequest("nonexistent.key"), CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error?.Message);
        var response = result.Value!;
        Assert.Equal("unknown", response.Type);
    }

    // ────────────────────────────────────────────────────────────────────
    // 3. ConfigSet_ValidValue_SetsAndReturns
    // ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ConfigSet_ValidValue_SetsAndReturns()
    {
        var svc = CreateService(CreateMinimalWorkspace());

        var result = await svc.ConfigSetAsync(new ConfigSetRequest("timeout.default", "60"), CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error?.Message);
        var response = result.Value!;
        Assert.Equal("60", response.Value);
        Assert.Null(response.PreviousValue);
    }

    // ────────────────────────────────────────────────────────────────────
    // 4. ConfigSet_InvalidType_ReturnsError
    // ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ConfigSet_InvalidType_ReturnsError()
    {
        var svc = CreateService(CreateMinimalWorkspace());

        var result = await svc.ConfigSetAsync(new ConfigSetRequest("timeout.default", "abc"), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.NotNull(result.Error);
        Assert.Contains("not a valid integer", result.Error!.Message);
    }

    // ────────────────────────────────────────────────────────────────────
    // 5. ConfigSet_UnknownKey_ReturnsError
    // ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ConfigSet_UnknownKey_ReturnsError()
    {
        var svc = CreateService(CreateMinimalWorkspace());

        var result = await svc.ConfigSetAsync(new ConfigSetRequest("completely.unknown.key", "value"), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.NotNull(result.Error);
        Assert.Contains("Unknown configuration key", result.Error!.Message);
    }

    // ────────────────────────────────────────────────────────────────────
    // 6. ConfigSet_DynamicToolKey_Succeeds
    // ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ConfigSet_DynamicToolKey_Succeeds()
    {
        var svc = CreateService(CreateMinimalWorkspace());

        var result = await svc.ConfigSetAsync(
            new ConfigSetRequest("tools.find_references.enabled", "false"), CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error?.Message);
        var response = result.Value!;
        Assert.Equal("false", response.Value);
    }

    // ────────────────────────────────────────────────────────────────────
    // 7. ConfigList_ReturnsAllDefinitions
    // ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ConfigList_ReturnsAllDefinitions()
    {
        var svc = CreateService(CreateMinimalWorkspace());

        var result = await svc.ConfigListAsync(new ConfigListRequest(), CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error?.Message);
        var response = result.Value!;
        Assert.True(response.Entries.Count >= 10,
            $"Expected at least 10 config entries but got {response.Entries.Count}");
    }

    // ────────────────────────────────────────────────────────────────────
    // 8. ToolEnabled_ToggleAndQuery
    // ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ToolEnabled_ToggleAndQuery()
    {
        var svc = CreateService(CreateMinimalWorkspace());

        // Disable the tool
        var disableResult = await svc.ToolEnabledAsync(
            new ToolEnabledRequest("find_references", Enabled: false), CancellationToken.None);

        Assert.True(disableResult.IsSuccess, disableResult.Error?.Message);
        var disableResponse = disableResult.Value!;
        Assert.True(disableResponse.WasChanged, "Expected WasChanged=true when disabling a tool for the first time");
        Assert.False(disableResponse.Enabled);

        // Query without setting (enabled=null)
        var queryResult = await svc.ToolEnabledAsync(
            new ToolEnabledRequest("find_references", Enabled: null), CancellationToken.None);

        Assert.True(queryResult.IsSuccess, queryResult.Error?.Message);
        var queryResponse = queryResult.Value!;
        Assert.False(queryResponse.Enabled, "Expected Enabled=false after disabling the tool");
    }
}
