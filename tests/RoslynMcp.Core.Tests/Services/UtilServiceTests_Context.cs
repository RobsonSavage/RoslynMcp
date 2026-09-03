using RoslynMcp.Core.Helpers;
using RoslynMcp.Core.Services;
using RoslynMcp.Core.Tests.Helpers;
using RoslynMcp.Shared.Contracts.Util;
using Serilog;
using Xunit;

namespace RoslynMcp.Core.Tests.Services;

public class UtilServiceTests_Context : IDisposable
{
    private readonly ILogger _logger = new LoggerConfiguration().CreateLogger();
    private WorkspaceTestHelper? _helper;
    private string? _tempConfigDir;

    private UtilService CreateService(WorkspaceTestHelper helper, string? configDir = null)
    {
        _helper = helper;
        var provider = helper.CreateProvider();
        var helpers = helper.CreateHelpers(_logger);
        _tempConfigDir = configDir ?? Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        var config = new ConfigManager(_tempConfigDir);
        return new UtilService(provider, helpers, config, new TestWorkspaceSelectionService(), _logger);
    }

    public void Dispose()
    {
        _helper?.Dispose();
        (_logger as IDisposable)?.Dispose();
        if (_tempConfigDir != null)
            try { Directory.Delete(_tempConfigDir, true); } catch { }
    }

    // ────────────────────────────────────────────────────────────────────
    // 1. GetFullContext_FindsCallersAndCallees
    // ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetFullContext_FindsCallersAndCallees()
    {
        var source = @"
namespace TestNs
{
    public class MyClass
    {
        public void MethodA()
        {
            MethodB();
        }

        public void MethodB() { }

        public void MethodC()
        {
            MethodA();
        }
    }
}";

        using var helper = new WorkspaceTestHelper();
        helper
            .AddProject("TestProject")
            .AddDocument("TestProject", "MyClass.cs", source);

        var service = CreateService(helper);

        // Line 5 = "        public void MethodA()"
        // Column 20 = start of "MethodA" identifier (8 spaces + "public void " = 20)
        var filePath = WorkspaceTestHelper.GetFilePath("TestProject", "MyClass.cs");
        var result = await service.GetFullContextAsync(
            new GetFullContextRequest(FilePath: filePath, Line: 5, Column: 20, Depth: 1),
            CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error?.Message);
        var response = result.Value!;

        Assert.Equal("MethodA", response.RootSymbol.Name);
        Assert.True(response.Context.Count >= 2,
            $"Expected at least 2 context nodes (caller + callee), got {response.Context.Count}");

        var relationships = response.Context.Select(n => n.Relationship).ToList();
        Assert.Contains("Caller", relationships);
        Assert.Contains("Callee", relationships);

        var callerNames = response.Context
            .Where(n => n.Relationship == "Caller")
            .Select(n => n.Symbol.Name).ToList();
        Assert.Contains("MethodC", callerNames);

        var calleeNames = response.Context
            .Where(n => n.Relationship == "Callee")
            .Select(n => n.Symbol.Name).ToList();
        Assert.Contains("MethodB", calleeNames);
    }

    // ────────────────────────────────────────────────────────────────────
    // 2. GetFullContext_RespectsDepthLimit
    // ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetFullContext_RespectsDepthLimit()
    {
        var source = @"
namespace TestNs
{
    public class MyClass
    {
        public void MethodA()
        {
            MethodB();
        }

        public void MethodB() { }

        public void MethodC()
        {
            MethodA();
        }
    }
}";

        using var helper = new WorkspaceTestHelper();
        helper
            .AddProject("TestProject")
            .AddDocument("TestProject", "MyClass.cs", source);

        var service = CreateService(helper);

        var filePath = WorkspaceTestHelper.GetFilePath("TestProject", "MyClass.cs");
        var result = await service.GetFullContextAsync(
            new GetFullContextRequest(FilePath: filePath, Line: 5, Column: 20, Depth: 0),
            CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error?.Message);
        var response = result.Value!;

        Assert.Equal("MethodA", response.RootSymbol.Name);
        Assert.Empty(response.Context);
    }

    // ────────────────────────────────────────────────────────────────────
    // 3. GetFullContext_ReturnsErrorForMissingDocument
    // ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetFullContext_ReturnsErrorForMissingDocument()
    {
        using var helper = new WorkspaceTestHelper();
        helper.AddProject("TestProject");

        var service = CreateService(helper);

        var filePath = WorkspaceTestHelper.GetFilePath("TestProject", "NonExistent.cs");
        var result = await service.GetFullContextAsync(
            new GetFullContextRequest(FilePath: filePath, Line: 0, Column: 0, Depth: 1),
            CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.NotNull(result.Error);
    }

    // ────────────────────────────────────────────────────────────────────
    // 4. GetQuickFixes_ReturnsEmptyForValidCode
    // ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetQuickFixes_ReturnsEmptyForValidCode()
    {
        var source = @"
namespace TestNs
{
    public class Valid
    {
        public void Method() { }
    }
}";

        using var helper = new WorkspaceTestHelper();
        helper
            .AddProject("TestProject")
            .AddDocument("TestProject", "Valid.cs", source);

        var service = CreateService(helper);

        // Line 5 = "        public void Method() { }"
        // Column 20 = start of "Method" identifier
        var filePath = WorkspaceTestHelper.GetFilePath("TestProject", "Valid.cs");
        var result = await service.GetQuickFixesAsync(
            new GetQuickFixesRequest(FilePath: filePath, Line: 5, Column: 20),
            CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error?.Message);
        var response = result.Value!;

        Assert.Empty(response.Fixes);
    }

    // ────────────────────────────────────────────────────────────────────
    // 5. SuggestRefactorings_SuggestsExtractMethodForLongMethod
    // ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task SuggestRefactorings_SuggestsExtractMethodForLongMethod()
    {
        var source = @"
namespace TestNs
{
    public class Complex
    {
        public void LongMethod()
        {
            int a = 1;
            int b = 2;
            int c = 3;
            int d = 4;
            int e = 5;
            int f = 6;
            int g = 7;
            int h = 8;
            int i = 9;
            int j = 10;
            int k = 11;
        }
    }
}";

        using var helper = new WorkspaceTestHelper();
        helper
            .AddProject("TestProject")
            .AddDocument("TestProject", "Complex.cs", source);

        var service = CreateService(helper);

        // Line 5 = "        public void LongMethod()"
        // Column 20 = start of "LongMethod" identifier
        var filePath = WorkspaceTestHelper.GetFilePath("TestProject", "Complex.cs");
        var result = await service.SuggestRefactoringsAsync(
            new SuggestRefactoringsRequest(FilePath: filePath, Line: 5, Column: 20),
            CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error?.Message);
        var response = result.Value!;

        var titles = response.Suggestions.Select(s => s.Title).ToList();
        Assert.Contains("Extract Method", titles);
    }

    // ────────────────────────────────────────────────────────────────────
    // 6. SuggestRefactorings_ReturnsEmptyForSimpleMethod
    // ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task SuggestRefactorings_ReturnsEmptyForSimpleMethod()
    {
        var source = @"
namespace TestNs
{
    public class Simple
    {
        public void ShortMethod()
        {
            int x = 1;
        }
    }
}";

        using var helper = new WorkspaceTestHelper();
        helper
            .AddProject("TestProject")
            .AddDocument("TestProject", "Simple.cs", source);

        var service = CreateService(helper);

        // Line 5 = "        public void ShortMethod()"
        // Column 20 = start of "ShortMethod" identifier
        var filePath = WorkspaceTestHelper.GetFilePath("TestProject", "Simple.cs");
        var result = await service.SuggestRefactoringsAsync(
            new SuggestRefactoringsRequest(FilePath: filePath, Line: 5, Column: 20),
            CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error?.Message);
        var response = result.Value!;

        Assert.Empty(response.Suggestions);
    }
}
