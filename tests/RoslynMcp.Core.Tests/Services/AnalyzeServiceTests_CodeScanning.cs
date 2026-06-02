using RoslynMcp.Core.Services;
using RoslynMcp.Core.Tests.Helpers;
using RoslynMcp.Shared.Contracts.Analyze;
using Serilog;
using Xunit;

namespace RoslynMcp.Core.Tests.Services;

public class AnalyzeServiceTests_CodeScanning : IDisposable
{
    private readonly ILogger _logger = new LoggerConfiguration().CreateLogger();
    private WorkspaceTestHelper? _helper;

    private AnalyzeService CreateService(WorkspaceTestHelper helper)
    {
        _helper = helper;
        var provider = helper.CreateProvider();
        var helpers = helper.CreateHelpers(_logger);
        return new AnalyzeService(provider, helpers, _logger);
    }

    public void Dispose()
    {
        _helper?.Dispose();
    }

    // ─── Test 1: ImpactAnalysis_FindsTransitiveCallers ───

    [Fact]
    public async Task ImpactAnalysis_FindsTransitiveCallers()
    {
        // Line 0: (empty)
        // Line 1: namespace TestNs
        // Line 2: {
        // Line 3:     public class Foo
        // Line 4:     {
        // Line 5:         public void C() { }
        // Line 6:         public void B() { C(); }
        // Line 7:         public void A() { B(); }
        // Line 8:     }
        // Line 9: }
        var source = @"
namespace TestNs
{
    public class Foo
    {
        public void C() { }
        public void B() { C(); }
        public void A() { B(); }
    }
}";
        var filePath = WorkspaceTestHelper.GetFilePath("TestProject", "Foo.cs");
        var helper = new WorkspaceTestHelper()
            .AddProject("TestProject")
            .AddDocument("TestProject", "Foo.cs", source);

        var service = CreateService(helper);

        // Point at "C" declaration: line 5
        // "        public void C() { }"
        //  01234567890123456789012
        //  ^^^^^^^^                 = 8 spaces
        //          ^^^^^^           = "public"
        //                ^          = space
        //                 ^^^^      = "void"
        //                     ^     = space
        //                      C    = col 20
        var request = new ImpactAnalysisRequest(filePath, Line: 5, Column: 20, Depth: 2);
        var result = await service.ImpactAnalysisAsync(request, CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error?.Message);
        var impacted = result.Value!.ImpactedSymbols.Items;
        var impactedNames = impacted.Select(n => n.Symbol.Name).ToList();
        Assert.Contains("B", impactedNames);
        Assert.Contains("A", impactedNames);
    }

    // ─── Test 2: ImpactAnalysis_RespectsDepthLimit ───

    [Fact]
    public async Task ImpactAnalysis_RespectsDepthLimit()
    {
        // Same source as Test 1: A calls B, B calls C
        var source = @"
namespace TestNs
{
    public class Foo
    {
        public void C() { }
        public void B() { C(); }
        public void A() { B(); }
    }
}";
        var filePath = WorkspaceTestHelper.GetFilePath("TestProject", "Foo.cs");
        var helper = new WorkspaceTestHelper()
            .AddProject("TestProject")
            .AddDocument("TestProject", "Foo.cs", source);

        var service = CreateService(helper);

        // Point at "C" declaration: line 5, col 20
        var request = new ImpactAnalysisRequest(filePath, Line: 5, Column: 20, Depth: 1);
        var result = await service.ImpactAnalysisAsync(request, CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error?.Message);
        var impacted = result.Value!.ImpactedSymbols.Items;
        var impactedNames = impacted.Select(n => n.Symbol.Name).ToList();
        Assert.Contains("B", impactedNames);
        Assert.DoesNotContain("A", impactedNames);
    }

    // ─── Test 3: FindUnusedCode_DetectsPrivateUnused ───

    [Fact]
    public async Task FindUnusedCode_DetectsPrivateUnused()
    {
        // Line 0: (empty)
        // Line 1: namespace TestNs
        // Line 2: {
        // Line 3:     public class Foo
        // Line 4:     {
        // Line 5:         private void Unused() { }
        // Line 6:         private void Used() { }
        // Line 7:         public void Entry() { Used(); }
        // Line 8:     }
        // Line 9: }
        var source = @"
namespace TestNs
{
    public class Foo
    {
        private void Unused() { }
        private void Used() { }
        public void Entry() { Used(); }
    }
}";
        var filePath = WorkspaceTestHelper.GetFilePath("TestProject", "Foo.cs");
        var helper = new WorkspaceTestHelper()
            .AddProject("TestProject")
            .AddDocument("TestProject", "Foo.cs", source);

        var service = CreateService(helper);

        var request = new FindUnusedCodeRequest(FilePath: filePath);
        var result = await service.FindUnusedCodeAsync(request, CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error?.Message);
        var unusedNames = result.Value!.UnusedItems.Items.Select(i => i.Symbol.Name).ToList();
        Assert.Contains("Unused", unusedNames);
        Assert.DoesNotContain("Used", unusedNames);
    }

    // ─── Test 4: FindUnusedCode_SkipsPublicMembers ───

    [Fact]
    public async Task FindUnusedCode_SkipsPublicMembers()
    {
        // Line 0: (empty)
        // Line 1: namespace TestNs
        // Line 2: {
        // Line 3:     public class Foo
        // Line 4:     {
        // Line 5:         public void UnusedPublic() { }
        // Line 6:     }
        // Line 7: }
        var source = @"
namespace TestNs
{
    public class Foo
    {
        public void UnusedPublic() { }
    }
}";
        var filePath = WorkspaceTestHelper.GetFilePath("TestProject", "Foo.cs");
        var helper = new WorkspaceTestHelper()
            .AddProject("TestProject")
            .AddDocument("TestProject", "Foo.cs", source);

        var service = CreateService(helper);

        var request = new FindUnusedCodeRequest(FilePath: filePath);
        var result = await service.FindUnusedCodeAsync(request, CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Empty(result.Value!.UnusedItems.Items);
    }

    // ─── Test 5: FindAsyncIssues_DetectsAsyncVoid ───

    [Fact]
    public async Task FindAsyncIssues_DetectsAsyncVoid()
    {
        // Line 0: using System.Threading.Tasks;
        // Line 1: (empty)
        // Line 2: namespace TestNs
        // Line 3: {
        // Line 4:     public class Foo
        // Line 5:     {
        // Line 6:         async void BadMethod() { await Task.Delay(1); }
        // Line 7:     }
        // Line 8: }
        var source = @"using System.Threading.Tasks;

namespace TestNs
{
    public class Foo
    {
        async void BadMethod() { await Task.Delay(1); }
    }
}";
        var filePath = WorkspaceTestHelper.GetFilePath("TestProject", "Foo.cs");
        var helper = new WorkspaceTestHelper()
            .AddProject("TestProject")
            .AddDocument("TestProject", "Foo.cs", source);

        var service = CreateService(helper);

        var request = new FindAsyncIssuesRequest(FilePath: filePath);
        var result = await service.FindAsyncIssuesAsync(request, CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error?.Message);
        var issues = result.Value!.Issues.Items;
        Assert.Contains(issues, i => i.IssueKind == "AsyncVoid");
    }

    // ─── Test 6: FindAsyncIssues_DetectsSyncOverAsync ───

    [Fact]
    public async Task FindAsyncIssues_DetectsSyncOverAsync()
    {
        // Line 0: using System.Threading.Tasks;
        // Line 1: (empty)
        // Line 2: namespace TestNs
        // Line 3: {
        // Line 4:     public class Foo
        // Line 5:     {
        // Line 6:         public int Bad()
        // Line 7:         {
        // Line 8:             var t = Task.FromResult(42);
        // Line 9:             return t.Result;
        // Line 10:        }
        // Line 11:    }
        // Line 12: }
        var source = @"using System.Threading.Tasks;

namespace TestNs
{
    public class Foo
    {
        public int Bad()
        {
            var t = Task.FromResult(42);
            return t.Result;
        }
    }
}";
        var filePath = WorkspaceTestHelper.GetFilePath("TestProject", "Foo.cs");
        var helper = new WorkspaceTestHelper()
            .AddProject("TestProject")
            .AddDocument("TestProject", "Foo.cs", source);

        var service = CreateService(helper);

        var request = new FindAsyncIssuesRequest(FilePath: filePath);
        var result = await service.FindAsyncIssuesAsync(request, CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error?.Message);
        var issues = result.Value!.Issues.Items;
        Assert.Contains(issues, i => i.IssueKind == "SyncOverAsync");
    }

    // ─── Test 7: FindPerformanceIssues_DetectsStringConcatInLoop ───

    [Fact]
    public async Task FindPerformanceIssues_DetectsStringConcatInLoop()
    {
        // Line 0: (empty)
        // Line 1: namespace TestNs
        // Line 2: {
        // Line 3:     public class Foo
        // Line 4:     {
        // Line 5:         public string Build()
        // Line 6:         {
        // Line 7:             var s = "";
        // Line 8:             for (int i = 0; i < 10; i++)
        // Line 9:             {
        // Line 10:                s = s + "x";
        // Line 11:            }
        // Line 12:            return s;
        // Line 13:        }
        // Line 14:    }
        // Line 15: }
        var source = @"
namespace TestNs
{
    public class Foo
    {
        public string Build()
        {
            var s = """";
            for (int i = 0; i < 10; i++)
            {
                s = s + ""x"";
            }
            return s;
        }
    }
}";
        var filePath = WorkspaceTestHelper.GetFilePath("TestProject", "Foo.cs");
        var helper = new WorkspaceTestHelper()
            .AddProject("TestProject")
            .AddDocument("TestProject", "Foo.cs", source);

        var service = CreateService(helper);

        var request = new FindPerformanceIssuesRequest(FilePath: filePath);
        var result = await service.FindPerformanceIssuesAsync(request, CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error?.Message);
        var issues = result.Value!.Issues.Items;
        Assert.Contains(issues, i => i.IssueKind == "StringConcatInLoop");
    }

    // ─── Test 8: FindPerformanceIssues_DetectsLinqInLoop ───

    [Fact]
    public async Task FindPerformanceIssues_DetectsLinqInLoop()
    {
        // Line 0: using System.Collections.Generic;
        // Line 1: using System.Linq;
        // Line 2: (empty)
        // Line 3: namespace TestNs
        // Line 4: {
        // Line 5:     public class Foo
        // Line 6:     {
        // Line 7:         public void Process()
        // Line 8:         {
        // Line 9:             var list = new List<int> { 1, 2, 3 };
        // Line 10:            for (int i = 0; i < 10; i++)
        // Line 11:            {
        // Line 12:                var filtered = list.Where(x => x > i).ToList();
        // Line 13:            }
        // Line 14:        }
        // Line 15:    }
        // Line 16: }
        var source = @"using System.Collections.Generic;
using System.Linq;

namespace TestNs
{
    public class Foo
    {
        public void Process()
        {
            var list = new List<int> { 1, 2, 3 };
            for (int i = 0; i < 10; i++)
            {
                var filtered = list.Where(x => x > i).ToList();
            }
        }
    }
}";
        var filePath = WorkspaceTestHelper.GetFilePath("TestProject", "Foo.cs");
        var helper = new WorkspaceTestHelper()
            .AddProject("TestProject")
            .AddDocument("TestProject", "Foo.cs", source);

        var service = CreateService(helper);

        var request = new FindPerformanceIssuesRequest(FilePath: filePath);
        var result = await service.FindPerformanceIssuesAsync(request, CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error?.Message);
        var issues = result.Value!.Issues.Items;
        Assert.Contains(issues, i => i.IssueKind == "LinqInLoop");
    }
}
