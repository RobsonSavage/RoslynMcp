using RoslynMcp.Core.Services;
using RoslynMcp.Core.Tests.Helpers;
using RoslynMcp.Shared.Contracts.Search;
using Serilog;
using Xunit;

namespace RoslynMcp.Core.Tests.Services;

public class SearchServiceTests_TypeHierarchy : IDisposable
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

    // ── FindOverrides ──

    [Fact]
    public async Task FindOverrides_FindsOverridingMethods()
    {
        // IWorker.cs layout:
        // Line 0: (empty)
        // Line 1: namespace TestNs;
        // Line 2: (empty)
        // Line 3: public interface IWorker
        // Line 4: {
        // Line 5:     void DoWork();
        // Line 6: }
        var interfaceSource = @"
namespace TestNs;

public interface IWorker
{
    void DoWork();
}
";
        // Worker.cs layout:
        // Line 0: (empty)
        // Line 1: namespace TestNs;
        // Line 2: (empty)
        // Line 3: public class ConcreteWorker : IWorker
        // Line 4: {
        // Line 5:     public void DoWork() { }
        // Line 6: }
        var implSource = @"
namespace TestNs;

public class ConcreteWorker : IWorker
{
    public void DoWork() { }
}
";
        using var helper = new WorkspaceTestHelper();
        helper
            .AddProject("TestProject")
            .AddDocument("TestProject", "IWorker.cs", interfaceSource)
            .AddDocument("TestProject", "Worker.cs", implSource);

        var service = CreateService(helper);
        var filePath = WorkspaceTestHelper.GetFilePath("TestProject", "IWorker.cs");

        // Point at "DoWork" on line 5 (interface method)
        // "    void DoWork();"
        //  col: 0123456789
        //           ^-- col 9 = 'D' of DoWork
        var request = new FindOverridesRequest(filePath, Line: 5, Column: 9);
        var result = await service.FindOverridesAsync(request);

        Assert.True(result.IsSuccess, $"Expected success but got error: {result.Error?.Message}");
        var response = result.Value!;
        Assert.Equal("DoWork", response.TargetSymbol.Name);
        Assert.True(response.Overrides.TotalCount >= 1,
            $"Expected at least 1 override but got {response.Overrides.TotalCount}");

        var overrideItem = response.Overrides.Items[0];
        Assert.Equal("DoWork", overrideItem.Symbol.Name);
        Assert.Contains("ConcreteWorker", overrideItem.ContainingType);
    }

    [Fact]
    public async Task FindOverrides_EmptyForNonVirtual()
    {
        // Source layout:
        // Line 0: (empty)
        // Line 1: namespace TestNs;
        // Line 2: (empty)
        // Line 3: public class MyClass
        // Line 4: {
        // Line 5:     public void RegularMethod() { }
        // Line 6: }
        var source = @"
namespace TestNs;

public class MyClass
{
    public void RegularMethod() { }
}
";
        using var helper = new WorkspaceTestHelper();
        helper
            .AddProject("TestProject")
            .AddDocument("TestProject", "NonVirtual.cs", source);

        var service = CreateService(helper);
        var filePath = WorkspaceTestHelper.GetFilePath("TestProject", "NonVirtual.cs");

        // Point at "RegularMethod" on line 5
        // "    public void RegularMethod() { }"
        //  col: 0123456789012345
        //                  ^-- col 16 = 'R' of RegularMethod
        var request = new FindOverridesRequest(filePath, Line: 5, Column: 16);
        var result = await service.FindOverridesAsync(request);

        Assert.True(result.IsSuccess);
        var response = result.Value!;
        Assert.Equal(0, response.Overrides.TotalCount);
    }

    // ── FindDerivedTypes ──

    [Fact]
    public async Task FindDerivedTypes_FindsDerivedClasses()
    {
        // Source layout:
        // Line 0: (empty)
        // Line 1: namespace TestNs;
        // Line 2: (empty)
        // Line 3: public class Animal { }
        // Line 4: (empty)
        // Line 5: public class Dog : Animal { }
        // Line 6: (empty)
        // Line 7: public class Cat : Animal { }
        var source = @"
namespace TestNs;

public class Animal { }

public class Dog : Animal { }

public class Cat : Animal { }
";
        using var helper = new WorkspaceTestHelper();
        helper
            .AddProject("TestProject")
            .AddDocument("TestProject", "Animals.cs", source);

        var service = CreateService(helper);
        var filePath = WorkspaceTestHelper.GetFilePath("TestProject", "Animals.cs");

        // Point at "Animal" on line 3
        // "public class Animal { }"
        //  col: 0123456789012345
        //               ^-- col 13 = 'A' of Animal
        var request = new FindDerivedTypesRequest(filePath, Line: 3, Column: 13);
        var result = await service.FindDerivedTypesAsync(request);

        Assert.True(result.IsSuccess);
        var response = result.Value!;
        Assert.Equal("Animal", response.TargetSymbol.Name);
        Assert.Equal(2, response.DerivedTypes.TotalCount);

        var names = response.DerivedTypes.Items.Select(d => d.Symbol.Name).OrderBy(n => n).ToList();
        Assert.Contains("Cat", names);
        Assert.Contains("Dog", names);
    }

    [Fact]
    public async Task FindDerivedTypes_FindsInterfaceImplementors()
    {
        // Source layout:
        // Line 0: (empty)
        // Line 1: namespace TestNs;
        // Line 2: (empty)
        // Line 3: public interface IShape
        // Line 4: {
        // Line 5:     double Area();
        // Line 6: }
        // Line 7: (empty)
        // Line 8: public class Circle : IShape
        // Line 9: {
        // Line 10:    public double Area() => 3.14;
        // Line 11: }
        // Line 12: (empty)
        // Line 13: public class Square : IShape
        // Line 14: {
        // Line 15:    public double Area() => 4.0;
        // Line 16: }
        var source = @"
namespace TestNs;

public interface IShape
{
    double Area();
}

public class Circle : IShape
{
    public double Area() => 3.14;
}

public class Square : IShape
{
    public double Area() => 4.0;
}
";
        using var helper = new WorkspaceTestHelper();
        helper
            .AddProject("TestProject")
            .AddDocument("TestProject", "Shapes.cs", source);

        var service = CreateService(helper);
        var filePath = WorkspaceTestHelper.GetFilePath("TestProject", "Shapes.cs");

        // Point at "IShape" on line 3
        // "public interface IShape"
        //  col: 0123456789012345678
        //                   ^-- col 17 = 'I' of IShape
        var request = new FindDerivedTypesRequest(filePath, Line: 3, Column: 17);
        var result = await service.FindDerivedTypesAsync(request);

        Assert.True(result.IsSuccess);
        var response = result.Value!;
        Assert.Equal("IShape", response.TargetSymbol.Name);
        Assert.Equal(2, response.DerivedTypes.TotalCount);

        var names = response.DerivedTypes.Items.Select(d => d.Symbol.Name).OrderBy(n => n).ToList();
        Assert.Contains("Circle", names);
        Assert.Contains("Square", names);
    }

    [Fact]
    public async Task FindDerivedTypes_ReturnsErrorForNonType()
    {
        // Source layout:
        // Line 0: (empty)
        // Line 1: namespace TestNs;
        // Line 2: (empty)
        // Line 3: public class Foo
        // Line 4: {
        // Line 5:     public void Bar() { }
        // Line 6: }
        var source = @"
namespace TestNs;

public class Foo
{
    public void Bar() { }
}
";
        using var helper = new WorkspaceTestHelper();
        helper
            .AddProject("TestProject")
            .AddDocument("TestProject", "NonType.cs", source);

        var service = CreateService(helper);
        var filePath = WorkspaceTestHelper.GetFilePath("TestProject", "NonType.cs");

        // Point at "Bar" method on line 5
        // "    public void Bar() { }"
        //  col: 0123456789012345
        //                  ^-- col 16 = 'B' of Bar
        var request = new FindDerivedTypesRequest(filePath, Line: 5, Column: 16);
        var result = await service.FindDerivedTypesAsync(request);

        Assert.False(result.IsSuccess);
        Assert.Contains("not a type", result.Error!.Message);
    }

    [Fact]
    public async Task FindDerivedTypes_MarksDirect()
    {
        // Source layout:
        // Line 0: (empty)
        // Line 1: namespace TestNs;
        // Line 2: (empty)
        // Line 3: public class Base { }
        // Line 4: (empty)
        // Line 5: public class Middle : Base { }
        // Line 6: (empty)
        // Line 7: public class Leaf : Middle { }
        var source = @"
namespace TestNs;

public class Base { }

public class Middle : Base { }

public class Leaf : Middle { }
";
        using var helper = new WorkspaceTestHelper();
        helper
            .AddProject("TestProject")
            .AddDocument("TestProject", "Chain.cs", source);

        var service = CreateService(helper);
        var filePath = WorkspaceTestHelper.GetFilePath("TestProject", "Chain.cs");

        // Point at "Base" on line 3
        // "public class Base { }"
        //  col: 0123456789012345
        //               ^-- col 13 = 'B' of Base
        var request = new FindDerivedTypesRequest(filePath, Line: 3, Column: 13);
        var result = await service.FindDerivedTypesAsync(request);

        Assert.True(result.IsSuccess);
        var response = result.Value!;
        Assert.Equal("Base", response.TargetSymbol.Name);
        Assert.Equal(2, response.DerivedTypes.TotalCount);

        var middle = response.DerivedTypes.Items.First(d => d.Symbol.Name == "Middle");
        var leaf = response.DerivedTypes.Items.First(d => d.Symbol.Name == "Leaf");

        Assert.True(middle.IsDirect, "Middle should be a direct derived type of Base");
        Assert.False(leaf.IsDirect, "Leaf should NOT be a direct derived type of Base");
    }

    // ── FindBaseMembers ──

    [Fact]
    public async Task FindBaseMembers_FindsOverriddenMethod()
    {
        // Source layout:
        // Line 0: (empty)
        // Line 1: namespace TestNs;
        // Line 2: (empty)
        // Line 3: public class BaseClass
        // Line 4: {
        // Line 5:     public virtual void Execute() { }
        // Line 6: }
        // Line 7: (empty)
        // Line 8: public class DerivedClass : BaseClass
        // Line 9: {
        // Line 10:    public override void Execute() { }
        // Line 11: }
        var source = @"
namespace TestNs;

public class BaseClass
{
    public virtual void Execute() { }
}

public class DerivedClass : BaseClass
{
    public override void Execute() { }
}
";
        using var helper = new WorkspaceTestHelper();
        helper
            .AddProject("TestProject")
            .AddDocument("TestProject", "Override.cs", source);

        var service = CreateService(helper);
        var filePath = WorkspaceTestHelper.GetFilePath("TestProject", "Override.cs");

        // Point at "Execute" on line 10 (the override in DerivedClass)
        // "    public override void Execute() { }"
        //  col: 01234567890123456789012345678
        //                           ^-- col 25 = 'E' of Execute
        var request = new FindBaseMembersRequest(filePath, Line: 10, Column: 25);
        var result = await service.FindBaseMembersAsync(request);

        Assert.True(result.IsSuccess);
        var response = result.Value!;
        Assert.Equal("Execute", response.TargetSymbol.Name);
        Assert.True(response.BaseMembers.Items.Count >= 1);

        var baseMember = response.BaseMembers.Items[0];
        Assert.Equal("Execute", baseMember.Symbol.Name);
        Assert.Equal("Override", baseMember.RelationKind);
    }

    [Fact]
    public async Task FindBaseMembers_FindsInterfaceImplementation()
    {
        // Source layout:
        // Line 0: (empty)
        // Line 1: namespace TestNs;
        // Line 2: (empty)
        // Line 3: public interface IFoo
        // Line 4: {
        // Line 5:     void DoSomething();
        // Line 6: }
        // Line 7: (empty)
        // Line 8: public class FooImpl : IFoo
        // Line 9: {
        // Line 10:    public void DoSomething() { }
        // Line 11: }
        var source = @"
namespace TestNs;

public interface IFoo
{
    void DoSomething();
}

public class FooImpl : IFoo
{
    public void DoSomething() { }
}
";
        using var helper = new WorkspaceTestHelper();
        helper
            .AddProject("TestProject")
            .AddDocument("TestProject", "Iface.cs", source);

        var service = CreateService(helper);
        var filePath = WorkspaceTestHelper.GetFilePath("TestProject", "Iface.cs");

        // Point at "DoSomething" on line 10 (the implementation in FooImpl)
        // "    public void DoSomething() { }"
        //  col: 0123456789012345
        //                  ^-- col 16 = 'D' of DoSomething
        var request = new FindBaseMembersRequest(filePath, Line: 10, Column: 16);
        var result = await service.FindBaseMembersAsync(request);

        Assert.True(result.IsSuccess);
        var response = result.Value!;
        Assert.Equal("DoSomething", response.TargetSymbol.Name);
        Assert.True(response.BaseMembers.Items.Count >= 1);

        var ifaceMember = response.BaseMembers.Items.First(b => b.RelationKind == "InterfaceImplementation");
        Assert.Equal("DoSomething", ifaceMember.Symbol.Name);
    }

    // ── FindEventSubscribers ──

    [Fact]
    public async Task FindEventSubscribers_FindsSubscriptions()
    {
        // Events.cs layout:
        // Line 0: (empty)
        // Line 1: using System;
        // Line 2: (empty)
        // Line 3: namespace TestNs;
        // Line 4: (empty)
        // Line 5: public delegate void DataHandler();
        // Line 6: (empty)
        // Line 7: public class Publisher
        // Line 8: {
        // Line 9:     public event DataHandler DataReady;
        // Line 10: (empty)
        // Line 11:    private void OnData() { }
        // Line 12: (empty)
        // Line 13:    public void Setup()
        // Line 14:    {
        // Line 15:        DataReady += OnData;
        // Line 16:    }
        // Line 17: }
        var source = @"
using System;

namespace TestNs;

public delegate void DataHandler();

public class Publisher
{
    public event DataHandler DataReady;

    private void OnData() { }

    public void Setup()
    {
        DataReady += OnData;
    }
}
";
        using var helper = new WorkspaceTestHelper();
        helper
            .AddProject("TestProject")
            .AddDocument("TestProject", "Events.cs", source);

        var service = CreateService(helper);
        var filePath = WorkspaceTestHelper.GetFilePath("TestProject", "Events.cs");

        // Point at "DataReady" event declaration on line 9
        // "    public event DataHandler DataReady;"
        //  col: 0         1         2         3
        //  col: 012345678901234567890123456789012
        //                               ^-- col 29 = 'D' of DataReady
        var request = new FindEventSubscribersRequest(filePath, Line: 9, Column: 29);
        var result = await service.FindEventSubscribersAsync(request);

        Assert.True(result.IsSuccess, $"Expected success but got error: {result.Error?.Message}");
        var response = result.Value!;
        Assert.Equal("DataReady", response.TargetEvent.Name);
        Assert.True(response.Subscribers.TotalCount >= 1,
            $"Expected at least 1 subscriber but got {response.Subscribers.TotalCount}");

        var subscriber = response.Subscribers.Items[0];
        Assert.Equal("Subscribe", subscriber.SubscriptionKind);
    }

    [Fact]
    public async Task FindEventSubscribers_ReturnsErrorForNonEvent()
    {
        // Source layout:
        // Line 0: (empty)
        // Line 1: namespace TestNs;
        // Line 2: (empty)
        // Line 3: public class MyClass
        // Line 4: {
        // Line 5:     public void SomeMethod() { }
        // Line 6: }
        var source = @"
namespace TestNs;

public class MyClass
{
    public void SomeMethod() { }
}
";
        using var helper = new WorkspaceTestHelper();
        helper
            .AddProject("TestProject")
            .AddDocument("TestProject", "NoEvent.cs", source);

        var service = CreateService(helper);
        var filePath = WorkspaceTestHelper.GetFilePath("TestProject", "NoEvent.cs");

        // Point at "SomeMethod" on line 5
        // "    public void SomeMethod() { }"
        //  col: 0123456789012345
        //                  ^-- col 16 = 'S' of SomeMethod
        var request = new FindEventSubscribersRequest(filePath, Line: 5, Column: 16);
        var result = await service.FindEventSubscribersAsync(request);

        Assert.False(result.IsSuccess);
        Assert.Contains("not an event", result.Error!.Message);
    }
}
