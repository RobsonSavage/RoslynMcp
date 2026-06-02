using RoslynMcp.Core.Services;
using RoslynMcp.Core.Tests.Helpers;
using RoslynMcp.Shared.Contracts.Structure;
using Serilog;
using Xunit;

namespace RoslynMcp.Core.Tests.Services;

public class StructureServiceTests_FileLevel : IDisposable
{
    private readonly ILogger _logger = new LoggerConfiguration().CreateLogger();
    private WorkspaceTestHelper? _helper;

    private StructureService CreateService(WorkspaceTestHelper helper)
    {
        _helper = helper;
        var provider = helper.CreateProvider();
        var helpers = helper.CreateHelpers(_logger);
        return new StructureService(provider, helpers, _logger);
    }

    public void Dispose() => _helper?.Dispose();

    // ─── Test 1: GetFileOutline_ReturnsTypeAndMembers ───

    [Fact]
    public async Task GetFileOutline_ReturnsTypeAndMembers()
    {
        var source = @"
namespace TestNs
{
    public class MyService
    {
        private int _count;
        public string Name { get; set; }
        public void Execute() { }
        public int Calculate(int x) { return x * 2; }
    }
}";
        var filePath = WorkspaceTestHelper.GetFilePath("TestProject", "MyService.cs");
        var helper = new WorkspaceTestHelper()
            .AddProject("TestProject")
            .AddDocument("TestProject", "MyService.cs", source);

        var service = CreateService(helper);

        var result = await service.GetFileOutlineAsync(
            new GetFileOutlineRequest(filePath), CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal(filePath, result.Value!.FilePath);

        // Top level: one class
        Assert.Single(result.Value.Items);
        var classItem = result.Value.Items[0];
        Assert.Equal("MyService", classItem.Name);
        Assert.Equal("Class", classItem.Kind);

        // Members: field, property, 2 methods
        Assert.Equal(4, classItem.Children.Count);

        var memberNames = classItem.Children.Select(c => c.Name).ToList();
        Assert.Contains("_count", memberNames);
        Assert.Contains("Name", memberNames);
        Assert.Contains("Execute", memberNames);
        Assert.Contains("Calculate", memberNames);

        // Verify return types
        var executeItem = classItem.Children.First(c => c.Name == "Execute");
        Assert.Equal("Method", executeItem.Kind);
        Assert.Equal("void", executeItem.ReturnType);

        var calculateItem = classItem.Children.First(c => c.Name == "Calculate");
        Assert.Equal("int", calculateItem.ReturnType);
    }

    // ─── Test 2: GetFileOutline_HandlesNestedTypes ───

    [Fact]
    public async Task GetFileOutline_HandlesNestedTypes()
    {
        var source = @"
namespace TestNs
{
    public class Outer
    {
        public void OuterMethod() { }

        public class Inner
        {
            public void InnerMethod() { }
        }
    }
}";
        var filePath = WorkspaceTestHelper.GetFilePath("TestProject", "Nested.cs");
        var helper = new WorkspaceTestHelper()
            .AddProject("TestProject")
            .AddDocument("TestProject", "Nested.cs", source);

        var service = CreateService(helper);

        var result = await service.GetFileOutlineAsync(
            new GetFileOutlineRequest(filePath), CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error?.Message);

        // Top-level: Outer class
        Assert.Single(result.Value!.Items);
        var outer = result.Value.Items[0];
        Assert.Equal("Outer", outer.Name);

        // Outer has 2 children: OuterMethod + Inner
        Assert.Equal(2, outer.Children.Count);

        var innerClass = outer.Children.FirstOrDefault(c => c.Name == "Inner");
        Assert.NotNull(innerClass);
        Assert.Equal("Class", innerClass.Kind);

        // Inner has 1 child: InnerMethod
        Assert.Single(innerClass.Children);
        Assert.Equal("InnerMethod", innerClass.Children[0].Name);
    }

    // ─── Test 3: GetFileOutline_HandlesEnum ───

    [Fact]
    public async Task GetFileOutline_HandlesEnum()
    {
        var source = @"
namespace TestNs
{
    public enum Status
    {
        Active,
        Inactive,
        Deleted
    }
}";
        var filePath = WorkspaceTestHelper.GetFilePath("TestProject", "Status.cs");
        var helper = new WorkspaceTestHelper()
            .AddProject("TestProject")
            .AddDocument("TestProject", "Status.cs", source);

        var service = CreateService(helper);

        var result = await service.GetFileOutlineAsync(
            new GetFileOutlineRequest(filePath), CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Single(result.Value!.Items);

        var enumItem = result.Value.Items[0];
        Assert.Equal("Status", enumItem.Name);
        Assert.Equal("Enum", enumItem.Kind);
        Assert.Equal(3, enumItem.Children.Count);

        var memberNames = enumItem.Children.Select(c => c.Name).ToList();
        Assert.Contains("Active", memberNames);
        Assert.Contains("Inactive", memberNames);
        Assert.Contains("Deleted", memberNames);
    }

    // ─── Test 4: GetTypesInFile_ReturnsAllTypes ───

    [Fact]
    public async Task GetTypesInFile_ReturnsAllTypes()
    {
        var source = @"
namespace TestNs
{
    public class ClassA { }
    public interface IServiceB { }
    public struct StructC { }
}";
        var filePath = WorkspaceTestHelper.GetFilePath("TestProject", "Types.cs");
        var helper = new WorkspaceTestHelper()
            .AddProject("TestProject")
            .AddDocument("TestProject", "Types.cs", source);

        var service = CreateService(helper);

        var result = await service.GetTypesInFileAsync(
            new GetTypesInFileRequest(filePath), CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal(3, result.Value!.Types.Count);

        var classA = result.Value.Types.First(t => t.Name == "ClassA");
        Assert.Equal("Class", classA.Kind);
        Assert.Equal("Public", classA.Accessibility);

        var ifaceB = result.Value.Types.First(t => t.Name == "IServiceB");
        Assert.Equal("Interface", ifaceB.Kind);

        var structC = result.Value.Types.First(t => t.Name == "StructC");
        Assert.Equal("Struct", structC.Kind);
    }

    // ─── Test 5: GetTypesInFile_ExcludesNestedWhenFlagFalse ───

    [Fact]
    public async Task GetTypesInFile_ExcludesNestedWhenFlagFalse()
    {
        var source = @"
namespace TestNs
{
    public class Outer
    {
        public class Nested { }
    }
}";
        var filePath = WorkspaceTestHelper.GetFilePath("TestProject", "OuterNested.cs");
        var helper = new WorkspaceTestHelper()
            .AddProject("TestProject")
            .AddDocument("TestProject", "OuterNested.cs", source);

        var service = CreateService(helper);

        // With IncludeNested = true (default)
        var resultAll = await service.GetTypesInFileAsync(
            new GetTypesInFileRequest(filePath, IncludeNested: true), CancellationToken.None);
        Assert.True(resultAll.IsSuccess);
        Assert.Equal(2, resultAll.Value!.Types.Count);

        // With IncludeNested = false
        var resultTopOnly = await service.GetTypesInFileAsync(
            new GetTypesInFileRequest(filePath, IncludeNested: false), CancellationToken.None);
        Assert.True(resultTopOnly.IsSuccess);
        Assert.Single(resultTopOnly.Value!.Types);
        Assert.Equal("Outer", resultTopOnly.Value.Types[0].Name);
    }

    // ─── Test 6: GetTypesInFile_DetectsModifiers ───

    [Fact]
    public async Task GetTypesInFile_DetectsModifiers()
    {
        var source = @"
namespace TestNs
{
    public abstract class AbstractBase { }
    public static class StaticHelper { }
}";
        var filePath = WorkspaceTestHelper.GetFilePath("TestProject", "Modifiers.cs");
        var helper = new WorkspaceTestHelper()
            .AddProject("TestProject")
            .AddDocument("TestProject", "Modifiers.cs", source);

        var service = CreateService(helper);

        var result = await service.GetTypesInFileAsync(
            new GetTypesInFileRequest(filePath), CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal(2, result.Value!.Types.Count);

        var abstractType = result.Value.Types.First(t => t.Name == "AbstractBase");
        Assert.True(abstractType.IsAbstract);
        Assert.False(abstractType.IsStatic);

        var staticType = result.Value.Types.First(t => t.Name == "StaticHelper");
        Assert.True(staticType.IsStatic);
        Assert.False(staticType.IsAbstract);
    }
}
