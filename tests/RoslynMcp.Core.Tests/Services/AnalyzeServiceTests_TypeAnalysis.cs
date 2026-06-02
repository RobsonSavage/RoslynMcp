using RoslynMcp.Core.Services;
using RoslynMcp.Core.Tests.Helpers;
using RoslynMcp.Shared.Contracts.Analyze;
using Serilog;
using Xunit;

namespace RoslynMcp.Core.Tests.Services;

public class AnalyzeServiceTests_TypeAnalysis : IDisposable
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

    // --- Test 1: UnderstandType_ReturnsAggregateInfo ---

    [Fact]
    public async Task UnderstandType_ReturnsAggregateInfo()
    {
        // Source layout (0-indexed lines):
        // Line 0: (empty)
        // Line 1: namespace TestNs
        // Line 2: {
        // Line 3:     public interface IRunnable
        // Line 4:     {
        // Line 5:         void Run();
        // Line 6:     }
        // Line 7:     public class BaseEntity
        // Line 8:     {
        // Line 9:     }
        // Line 10:    public class Worker : BaseEntity, IRunnable
        // Line 11:    {
        // Line 12:        public string Name { get; set; }
        // Line 13:        public void Run() { }
        // Line 14:        public void Stop() { }
        // Line 15:    }
        // Line 16: }
        var source = @"
namespace TestNs
{
    public interface IRunnable
    {
        void Run();
    }
    public class BaseEntity
    {
    }
    public class Worker : BaseEntity, IRunnable
    {
        public string Name { get; set; }
        public void Run() { }
        public void Stop() { }
    }
}";
        var filePath = WorkspaceTestHelper.GetFilePath("TestProject", "Worker.cs");
        var helper = new WorkspaceTestHelper()
            .AddProject("TestProject")
            .AddDocument("TestProject", "Worker.cs", source);

        var service = CreateService(helper);

        // Point at "Worker" on line 10
        // "    public class Worker : BaseEntity, IRunnable"
        //  01234567890123456789
        //  ^^^^                 = 4 spaces
        //      ^^^^^^           = "public"
        //            ^          = space
        //             ^^^^^     = "class"
        //                  ^    = space
        //                   W   = col 17
        var request = new UnderstandTypeRequest(FilePath: filePath, Line: 10, Column: 17);
        var result = await service.UnderstandTypeAsync(request, CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error?.Message);
        var response = result.Value!;
        Assert.Equal("Class", response.TypeKind);
        Assert.Contains(response.BaseTypes, b => b.Contains("BaseEntity"));
        Assert.Contains(response.Interfaces, i => i.Contains("IRunnable"));
        Assert.True(response.Members.Count >= 2, $"Expected at least 2 members, got {response.Members.Count}");
        Assert.True(response.UsageCount >= 0);
    }

    // --- Test 2: UnderstandType_FindsByName ---

    [Fact]
    public async Task UnderstandType_FindsByName()
    {
        // Line 0: (empty)
        // Line 1: namespace TestNs
        // Line 2: {
        // Line 3:     public class Gadget
        // Line 4:     {
        // Line 5:         public int Id { get; set; }
        // Line 6:         public void Activate() { }
        // Line 7:     }
        // Line 8: }
        var source = @"
namespace TestNs
{
    public class Gadget
    {
        public int Id { get; set; }
        public void Activate() { }
    }
}";
        var helper = new WorkspaceTestHelper()
            .AddProject("TestProject")
            .AddDocument("TestProject", "Gadget.cs", source);

        var service = CreateService(helper);

        // Use TypeName instead of FilePath/Line/Column
        var request = new UnderstandTypeRequest(TypeName: "TestNs.Gadget");
        var result = await service.UnderstandTypeAsync(request, CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error?.Message);
        var response = result.Value!;
        Assert.Equal("Gadget", response.Symbol.Name);
        Assert.Equal("TestNs", response.Symbol.ContainingNamespace);
        Assert.True(response.Members.Count >= 2, $"Expected at least 2 members, got {response.Members.Count}");
    }

    // --- Test 3: GetTypeInfo_ReturnsDetails ---

    [Fact]
    public async Task GetTypeInfo_ReturnsDetails()
    {
        // Line 0: (empty)
        // Line 1: namespace TestNs
        // Line 2: {
        // Line 3:     public abstract class Repository<T>
        // Line 4:     {
        // Line 5:         public abstract T GetById(int id);
        // Line 6:         public virtual void Save(T item) { }
        // Line 7:     }
        // Line 8: }
        var source = @"
namespace TestNs
{
    public abstract class Repository<T>
    {
        public abstract T GetById(int id);
        public virtual void Save(T item) { }
    }
}";
        var filePath = WorkspaceTestHelper.GetFilePath("TestProject", "Repository.cs");
        var helper = new WorkspaceTestHelper()
            .AddProject("TestProject")
            .AddDocument("TestProject", "Repository.cs", source);

        var service = CreateService(helper);

        // Point at "Repository" on line 3
        // "    public abstract class Repository<T>"
        //  0123456789012345678901234567
        //  ^^^^                         = 4 spaces
        //      ^^^^^^                   = "public"
        //            ^                  = space
        //             ^^^^^^^^          = "abstract"
        //                     ^         = space
        //                      ^^^^^    = "class"
        //                           ^   = space
        //                            R  = col 26
        var request = new GetTypeInfoRequest(FilePath: filePath, Line: 3, Column: 26);
        var result = await service.GetTypeInfoAsync(request, CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error?.Message);
        var response = result.Value!;
        Assert.Equal("Class", response.TypeKind);
        Assert.True(response.IsAbstract);
        Assert.True(response.IsGeneric);
        Assert.Equal(1, response.GenericParameterCount);
        Assert.True(response.Members.Count >= 2, $"Expected at least 2 members, got {response.Members.Count}");
    }

    // --- Test 4: GetTypeInfo_StaticClass ---

    [Fact]
    public async Task GetTypeInfo_StaticClass()
    {
        // Line 0: (empty)
        // Line 1: namespace TestNs
        // Line 2: {
        // Line 3:     public static class MathUtils
        // Line 4:     {
        // Line 5:         public static int Add(int a, int b) => a + b;
        // Line 6:         public static int Multiply(int a, int b) => a * b;
        // Line 7:     }
        // Line 8: }
        var source = @"
namespace TestNs
{
    public static class MathUtils
    {
        public static int Add(int a, int b) => a + b;
        public static int Multiply(int a, int b) => a * b;
    }
}";
        var filePath = WorkspaceTestHelper.GetFilePath("TestProject", "MathUtils.cs");
        var helper = new WorkspaceTestHelper()
            .AddProject("TestProject")
            .AddDocument("TestProject", "MathUtils.cs", source);

        var service = CreateService(helper);

        // Point at "MathUtils" on line 3
        // "    public static class MathUtils"
        //  01234567890123456789012345
        //  ^^^^                       = 4 spaces
        //      ^^^^^^                 = "public"
        //            ^                = space
        //             ^^^^^^          = "static"
        //                   ^         = space
        //                    ^^^^^    = "class"
        //                         ^   = space
        //                          M  = col 24
        var request = new GetTypeInfoRequest(FilePath: filePath, Line: 3, Column: 24);
        var result = await service.GetTypeInfoAsync(request, CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error?.Message);
        var response = result.Value!;
        Assert.True(response.IsStatic);
        // Note: Roslyn's IsSealed returns false for static classes at the symbol level,
        // even though the CLR marks them as sealed+abstract. IsStatic is the authoritative check.
        Assert.False(response.IsAbstract, "Static classes should not report IsAbstract via Roslyn");
        Assert.Equal("Class", response.TypeKind);
    }

    // --- Test 5: GetClassHierarchy_ReturnsAncestorsAndDescendants ---

    [Fact]
    public async Task GetClassHierarchy_ReturnsAncestorsAndDescendants()
    {
        // Line 0: (empty)
        // Line 1: namespace TestNs
        // Line 2: {
        // Line 3:     public class Base
        // Line 4:     {
        // Line 5:     }
        // Line 6:     public class Middle : Base
        // Line 7:     {
        // Line 8:     }
        // Line 9:     public class Derived : Middle
        // Line 10:    {
        // Line 11:    }
        // Line 12: }
        var source = @"
namespace TestNs
{
    public class Base
    {
    }
    public class Middle : Base
    {
    }
    public class Derived : Middle
    {
    }
}";
        var filePath = WorkspaceTestHelper.GetFilePath("TestProject", "Hierarchy.cs");
        var helper = new WorkspaceTestHelper()
            .AddProject("TestProject")
            .AddDocument("TestProject", "Hierarchy.cs", source);

        var service = CreateService(helper);

        // Point at "Middle" on line 6
        // "    public class Middle : Base"
        //  01234567890123456789
        //  ^^^^                 = 4 spaces
        //      ^^^^^^           = "public"
        //            ^          = space
        //             ^^^^^     = "class"
        //                  ^    = space
        //                   M   = col 17
        var request = new GetClassHierarchyRequest(FilePath: filePath, Line: 6, Column: 17);
        var result = await service.GetClassHierarchyAsync(request, CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error?.Message);
        var response = result.Value!;

        // Ancestors should contain Base
        Assert.True(response.Ancestors.Count >= 1, $"Expected at least 1 ancestor, got {response.Ancestors.Count}");
        Assert.Contains(response.Ancestors, a => a.Symbol.Name == "Base");

        // Descendants should contain Derived
        Assert.True(response.Descendants.Count >= 1, $"Expected at least 1 descendant, got {response.Descendants.Count}");
        Assert.Contains(response.Descendants, d => d.Symbol.Name == "Derived");
    }

    // --- Test 6: GetClassHierarchy_InterfaceDescendants ---

    [Fact]
    public async Task GetClassHierarchy_InterfaceDescendants()
    {
        // Line 0: (empty)
        // Line 1: namespace TestNs
        // Line 2: {
        // Line 3:     public interface IShape
        // Line 4:     {
        // Line 5:         double Area();
        // Line 6:     }
        // Line 7:     public class Circle : IShape
        // Line 8:     {
        // Line 9:         public double Area() => 3.14;
        // Line 10:    }
        // Line 11:    public class Square : IShape
        // Line 12:    {
        // Line 13:        public double Area() => 4.0;
        // Line 14:    }
        // Line 15: }
        var source = @"
namespace TestNs
{
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
}";
        var filePath = WorkspaceTestHelper.GetFilePath("TestProject", "Shapes.cs");
        var helper = new WorkspaceTestHelper()
            .AddProject("TestProject")
            .AddDocument("TestProject", "Shapes.cs", source);

        var service = CreateService(helper);

        // Point at "IShape" on line 3
        // "    public interface IShape"
        //  0123456789012345678901
        //  ^^^^                   = 4 spaces
        //      ^^^^^^             = "public"
        //            ^            = space
        //             ^^^^^^^^^   = "interface"
        //                      ^  = space
        //                       I = col 21
        var request = new GetClassHierarchyRequest(FilePath: filePath, Line: 3, Column: 21);
        var result = await service.GetClassHierarchyAsync(request, CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error?.Message);
        var response = result.Value!;

        // Descendants should contain both Circle and Square
        Assert.True(response.Descendants.Count >= 2, $"Expected at least 2 descendants, got {response.Descendants.Count}");
        Assert.Contains(response.Descendants, d => d.Symbol.Name == "Circle");
        Assert.Contains(response.Descendants, d => d.Symbol.Name == "Square");
    }

    // --- Test 7: GetTypeMembers_WithKindFilter ---

    [Fact]
    public async Task GetTypeMembers_WithKindFilter()
    {
        // Line 0: (empty)
        // Line 1: namespace TestNs
        // Line 2: {
        // Line 3:     public class Mixed
        // Line 4:     {
        // Line 5:         public int Count;
        // Line 6:         public string Label { get; set; }
        // Line 7:         public void Execute() { }
        // Line 8:         public int Calculate() => 42;
        // Line 9:     }
        // Line 10: }
        var source = @"
namespace TestNs
{
    public class Mixed
    {
        public int Count;
        public string Label { get; set; }
        public void Execute() { }
        public int Calculate() => 42;
    }
}";
        var filePath = WorkspaceTestHelper.GetFilePath("TestProject", "Mixed.cs");
        var helper = new WorkspaceTestHelper()
            .AddProject("TestProject")
            .AddDocument("TestProject", "Mixed.cs", source);

        var service = CreateService(helper);

        // Point at "Mixed" on line 3
        // "    public class Mixed"
        //  01234567890123456789
        //  ^^^^                 = 4 spaces
        //      ^^^^^^           = "public"
        //            ^          = space
        //             ^^^^^     = "class"
        //                  ^    = space
        //                   M   = col 17
        var request = new GetTypeMembersRequest(
            FilePath: filePath, Line: 3, Column: 17,
            KindFilter: "Method",
            PageSize: 50);
        var result = await service.GetTypeMembersAsync(request, CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error?.Message);
        var response = result.Value!;

        // All returned members should be methods
        Assert.True(response.Members.Items.Count >= 2, $"Expected at least 2 methods, got {response.Members.Items.Count}");
        Assert.All(response.Members.Items, m =>
            Assert.Equal("Method", m.Kind));
    }

    // --- Test 8: GetTypeMembers_WithInherited ---

    [Fact]
    public async Task GetTypeMembers_WithInherited()
    {
        // Line 0: (empty)
        // Line 1: namespace TestNs
        // Line 2: {
        // Line 3:     public class ParentClass
        // Line 4:     {
        // Line 5:         public void BaseMethod() { }
        // Line 6:     }
        // Line 7:     public class ChildClass : ParentClass
        // Line 8:     {
        // Line 9:         public void DerivedMethod() { }
        // Line 10:    }
        // Line 11: }
        var source = @"
namespace TestNs
{
    public class ParentClass
    {
        public void BaseMethod() { }
    }
    public class ChildClass : ParentClass
    {
        public void DerivedMethod() { }
    }
}";
        var filePath = WorkspaceTestHelper.GetFilePath("TestProject", "Inheritance.cs");
        var helper = new WorkspaceTestHelper()
            .AddProject("TestProject")
            .AddDocument("TestProject", "Inheritance.cs", source);

        var service = CreateService(helper);

        // Point at "ChildClass" on line 7
        // "    public class ChildClass : ParentClass"
        //  01234567890123456789
        //  ^^^^                 = 4 spaces
        //      ^^^^^^           = "public"
        //            ^          = space
        //             ^^^^^     = "class"
        //                  ^    = space
        //                   C   = col 17
        var request = new GetTypeMembersRequest(
            FilePath: filePath, Line: 7, Column: 17,
            IncludeInherited: true,
            PageSize: 50);
        var result = await service.GetTypeMembersAsync(request, CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error?.Message);
        var response = result.Value!;

        var memberNames = response.Members.Items.Select(m => m.Name).ToList();
        Assert.Contains("DerivedMethod", memberNames);
        Assert.Contains("BaseMethod", memberNames);
    }

    // --- Test 9: UnderstandType_FindsBySimpleName ---

    [Fact]
    public async Task UnderstandType_FindsBySimpleName()
    {
        var source = @"
namespace TestNs
{
    public class Widget
    {
        public int Value { get; set; }
    }
}";
        var helper = new WorkspaceTestHelper()
            .AddProject("TestProject")
            .AddDocument("TestProject", "Widget.cs", source);

        var service = CreateService(helper);
        var request = new UnderstandTypeRequest(TypeName: "Widget"); // simple name, not "TestNs.Widget"
        var result = await service.UnderstandTypeAsync(request, CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal("Widget", result.Value!.Symbol.Name);
        Assert.Equal("TestNs", result.Value!.Symbol.ContainingNamespace);
    }

    // --- Test 10: UnderstandType_NotFound_ReturnsError ---

    [Fact]
    public async Task UnderstandType_NotFound_ReturnsError()
    {
        var source = @"
namespace TestNs
{
    public class Existing { }
}";
        var helper = new WorkspaceTestHelper()
            .AddProject("TestProject")
            .AddDocument("TestProject", "Existing.cs", source);

        var service = CreateService(helper);
        var request = new UnderstandTypeRequest(TypeName: "NonExistentType");
        var result = await service.UnderstandTypeAsync(request, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Contains("Type not found", result.Error!.Message);
    }

    // --- Test 11: UnderstandType_AmbiguousSimpleName_ReturnsFirst ---

    [Fact]
    public async Task UnderstandType_AmbiguousSimpleName_ReturnsFirst()
    {
        var source = @"
namespace NsA
{
    public class Widget { public int A { get; set; } }
}
namespace NsB
{
    public class Widget { public int B { get; set; } }
}";
        var helper = new WorkspaceTestHelper()
            .AddProject("TestProject")
            .AddDocument("TestProject", "Widgets.cs", source);

        var service = CreateService(helper);
        var request = new UnderstandTypeRequest(TypeName: "Widget");
        var result = await service.UnderstandTypeAsync(request, CancellationToken.None);

        // Should succeed — picks one of the two (order unspecified but deterministic per compilation)
        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal("Widget", result.Value!.Symbol.Name);
    }

    // --- Test 12: UnderstandType_NoTypeName_NoLocation_ReturnsError ---

    [Fact]
    public async Task UnderstandType_NoTypeName_NoLocation_ReturnsError()
    {
        var helper = new WorkspaceTestHelper()
            .AddProject("TestProject");

        var service = CreateService(helper);
        var request = new UnderstandTypeRequest(); // no TypeName, no FilePath/Line/Column
        var result = await service.UnderstandTypeAsync(request, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Contains("must be provided", result.Error!.Message);
    }
}
