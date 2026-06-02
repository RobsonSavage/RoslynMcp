using RoslynMcp.Core.Services;
using RoslynMcp.Core.Tests.Helpers;
using RoslynMcp.Shared.Contracts.Search;
using Serilog;
using Xunit;

namespace RoslynMcp.Core.Tests.Services;

public class SearchServiceTests_DeferredEnrichment : IDisposable
{
    private readonly ILogger _logger = new LoggerConfiguration().CreateLogger();

    private SearchService CreateService(WorkspaceTestHelper helper)
    {
        var provider = helper.CreateProvider();
        var helpers = helper.CreateHelpers(_logger);
        return new SearchService(provider, helpers, _logger);
    }

    public void Dispose()
    {
        (_logger as IDisposable)?.Dispose();
    }

    private const string SharedSource = @"
namespace TestNs
{
    public class MyBase
    {
        public virtual void DoWork() { }
        public int Field;
        public void SetField() { Field = 42; }
    }

    public class MyDerived : MyBase
    {
        public override void DoWork() { }
    }

    public class Consumer
    {
        public void Use()
        {
            var b = new MyBase();
            b.DoWork();
            var d = new MyDerived();
            d.DoWork();
        }
    }
}";

    private const string AttributeSource = @"
using System;
namespace TestNs
{
    [AttributeUsage(AttributeTargets.Class)]
    public class MyAttrAttribute : Attribute { }

    [MyAttr]
    public class Decorated1 { }

    [MyAttr]
    public class Decorated2 { }
}";

    private const string EventSource = @"
using System;
namespace TestNs
{
    public class Publisher
    {
        public event EventHandler MyEvent;
        public void AddHandler(EventHandler h) { MyEvent += h; }
        public void RemoveHandler(EventHandler h) { MyEvent -= h; }
    }
}";

    [Fact]
    public async Task FindReferences_IsWriteAccess_PopulatedForPagedItems()
    {
        using var helper = new WorkspaceTestHelper();
        helper.AddProject("TestProject")
              .AddDocument("TestProject", "Code.cs", SharedSource);

        var service = CreateService(helper);
        var filePath = WorkspaceTestHelper.GetFilePath("TestProject", "Code.cs");
        var (line, col) = WorkspaceTestHelper.FindPosition(SharedSource, "Field");

        var result = await service.FindReferencesAsync(
            new FindReferencesRequest(filePath, line, col, IncludeContext: false));

        Assert.True(result.IsSuccess);
        var refs = result.Value!.References;
        Assert.True(refs.TotalCount > 0);
        // At least one reference should have IsWriteAccess = true (the "b.Field = 42" line)
        Assert.Contains(refs.Items, r => r.IsWriteAccess);
    }

    [Fact]
    public async Task FindReferences_PageTwo_ContextEnriched()
    {
        // Create a source with many references so we can test page 2
        var source = @"
namespace TestNs
{
    public class Foo
    {
        public int Val;
    }

    public class Bar
    {
        public void M()
        {
            var f = new Foo();
            var x1 = f.Val;
            var x2 = f.Val;
            var x3 = f.Val;
            var x4 = f.Val;
            var x5 = f.Val;
            var x6 = f.Val;
        }
    }
}";
        using var helper = new WorkspaceTestHelper();
        helper.AddProject("TestProject")
              .AddDocument("TestProject", "Code.cs", source);

        var service = CreateService(helper);
        var filePath = WorkspaceTestHelper.GetFilePath("TestProject", "Code.cs");
        var (line, col) = WorkspaceTestHelper.FindPosition(source, "Val");

        // Page 2 with pageSize=2
        var result = await service.FindReferencesAsync(
            new FindReferencesRequest(filePath, line, col, IncludeContext: true, PageSize: 2, Page: 1));

        Assert.True(result.IsSuccess);
        var refs = result.Value!.References;
        Assert.True(refs.TotalCount > 2, $"Expected >2 refs, got {refs.TotalCount}");
        Assert.Equal(1, refs.Page);
        // Context should be populated for paged items
        foreach (var item in refs.Items)
        {
            Assert.NotNull(item.ContextLine);
        }
    }

    [Fact]
    public async Task FindAttributeUsages_DecoratedSymbolResolved()
    {
        using var helper = new WorkspaceTestHelper();
        helper.AddProject("TestProject")
              .AddDocument("TestProject", "Code.cs", AttributeSource);

        var service = CreateService(helper);

        var result = await service.FindAttributeUsagesAsync(
            new FindAttributeUsagesRequest("MyAttr"));

        Assert.True(result.IsSuccess);
        var usages = result.Value!.Usages;
        Assert.True(usages.TotalCount >= 2);
        // Decorated symbols should be resolved (not "Unknown")
        foreach (var item in usages.Items)
        {
            Assert.NotEqual("Unknown", item.DecoratedSymbol.Name);
        }
    }

    [Fact]
    public async Task FindEventSubscribers_FiltersNonSubscriptions()
    {
        using var helper = new WorkspaceTestHelper();
        helper.AddProject("TestProject")
              .AddDocument("TestProject", "Code.cs", EventSource);

        var service = CreateService(helper);
        var filePath = WorkspaceTestHelper.GetFilePath("TestProject", "Code.cs");
        var (line, col) = WorkspaceTestHelper.FindPosition(EventSource, "MyEvent");

        var result = await service.FindEventSubscribersAsync(
            new FindEventSubscribersRequest(filePath, line, col));

        Assert.True(result.IsSuccess);
        var subs = result.Value!.Subscribers;
        // Should only include += and -= (not plain references)
        Assert.True(subs.TotalCount >= 2);
        foreach (var item in subs.Items)
        {
            Assert.True(
                item.SubscriptionKind == "Subscribe" || item.SubscriptionKind == "Unsubscribe",
                $"Expected Subscribe/Unsubscribe, got {item.SubscriptionKind}");
        }
    }

    [Fact]
    public async Task FindImplementations_ContextLine_OnlyWhenRequested()
    {
        using var helper = new WorkspaceTestHelper();
        helper.AddProject("TestProject")
              .AddDocument("TestProject", "Code.cs", SharedSource);

        var service = CreateService(helper);
        var filePath = WorkspaceTestHelper.GetFilePath("TestProject", "Code.cs");
        var (line, col) = WorkspaceTestHelper.FindPosition(SharedSource, "DoWork");

        // Without IncludeContext
        var result = await service.FindImplementationsAsync(
            new FindImplementationsRequest(filePath, line, col, IncludeContext: false));

        Assert.True(result.IsSuccess);
        if (result.Value!.Implementations.Items.Count > 0)
        {
            foreach (var item in result.Value!.Implementations.Items)
            {
                Assert.Null(item.ContextLine);
            }
        }

        // With IncludeContext
        var resultCtx = await service.FindImplementationsAsync(
            new FindImplementationsRequest(filePath, line, col, IncludeContext: true));

        Assert.True(resultCtx.IsSuccess);
        if (resultCtx.Value!.Implementations.Items.Count > 0)
        {
            // At least one should have context
            Assert.Contains(resultCtx.Value!.Implementations.Items, i => i.ContextLine != null);
        }
    }

    [Fact]
    public async Task PageAndEnrichAsync_ConcurrentCalls_SameDocument_Correct()
    {
        using var helper = new WorkspaceTestHelper();
        helper.AddProject("TestProject")
              .AddDocument("TestProject", "Code.cs", SharedSource);

        var service = CreateService(helper);
        var filePath = WorkspaceTestHelper.GetFilePath("TestProject", "Code.cs");
        var (line, col) = WorkspaceTestHelper.FindPosition(SharedSource, "Field");

        // Run 3 concurrent FindReferences calls
        var tasks = Enumerable.Range(0, 3).Select(_ =>
            service.FindReferencesAsync(
                new FindReferencesRequest(filePath, line, col, IncludeContext: true)));

        var results = await Task.WhenAll(tasks);

        // All should succeed with same results
        foreach (var result in results)
        {
            Assert.True(result.IsSuccess);
            Assert.True(result.Value!.References.TotalCount > 0);
        }

        // Result counts should match
        var counts = results.Select(r => r.Value!.References.TotalCount).Distinct().ToList();
        Assert.Single(counts); // All should have the same count
    }

}
