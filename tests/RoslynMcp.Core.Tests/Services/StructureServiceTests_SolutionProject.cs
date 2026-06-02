using RoslynMcp.Core.Services;
using RoslynMcp.Core.Tests.Helpers;
using RoslynMcp.Shared.Contracts.Structure;
using Serilog;
using Xunit;

namespace RoslynMcp.Core.Tests.Services;

public class StructureServiceTests_SolutionProject : IDisposable
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

    // ─── Test 1: GetSolutionStructure_ReturnsAllProjects ───

    [Fact]
    public async Task GetSolutionStructure_ReturnsAllProjects()
    {
        var helper = new WorkspaceTestHelper()
            .AddProject("ProjectA")
            .AddDocument("ProjectA", "ClassA.cs", "namespace A { public class ClassA { } }")
            .AddProject("ProjectB")
            .AddDocument("ProjectB", "ClassB.cs", "namespace B { public class ClassB { } }");

        var service = CreateService(helper);

        var result = await service.GetSolutionStructureAsync(
            new GetSolutionStructureRequest(), CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal(2, result.Value!.Projects.Count);

        var names = result.Value.Projects.Select(p => p.Name).OrderBy(n => n).ToList();
        Assert.Equal("ProjectA", names[0]);
        Assert.Equal("ProjectB", names[1]);
    }

    // ─── Test 2: GetSolutionStructure_ReturnsDocumentCounts ───

    [Fact]
    public async Task GetSolutionStructure_ReturnsDocumentCounts()
    {
        var helper = new WorkspaceTestHelper()
            .AddProject("MyProject")
            .AddDocument("MyProject", "File1.cs", "class A { }")
            .AddDocument("MyProject", "File2.cs", "class B { }")
            .AddDocument("MyProject", "File3.cs", "class C { }");

        var service = CreateService(helper);

        var result = await service.GetSolutionStructureAsync(
            new GetSolutionStructureRequest(), CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error?.Message);
        var project = result.Value!.Projects.Single();
        Assert.Equal("MyProject", project.Name);
        Assert.Equal(3, project.DocumentCount);
    }

    // ─── Test 3: GetProjectStructure_ReturnsDocumentsAndReferences ───

    [Fact]
    public async Task GetProjectStructure_ReturnsDocumentsAndReferences()
    {
        var helper = new WorkspaceTestHelper()
            .AddProject("Core")
            .AddDocument("Core", "Entity.cs", "namespace Core { public class Entity { } }")
            .AddProject("App", "Core")
            .AddDocument("App", "Service.cs", "namespace App { public class Service { } }");

        var service = CreateService(helper);

        var result = await service.GetProjectStructureAsync(
            new GetProjectStructureRequest("App"), CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.Equal("App", result.Value!.Project.Name);

        // Documents
        Assert.Single(result.Value.Documents);
        Assert.Contains("Service.cs", result.Value.Documents[0].FilePath);

        // Project references
        Assert.Single(result.Value.ProjectReferences);
        Assert.Equal("Core", result.Value.ProjectReferences[0].ProjectName);
    }

    // ─── Test 4: GetProjectStructure_ReturnsErrorForUnknownProject ───

    [Fact]
    public async Task GetProjectStructure_ReturnsErrorForUnknownProject()
    {
        var helper = new WorkspaceTestHelper()
            .AddProject("Existing");

        var service = CreateService(helper);

        var result = await service.GetProjectStructureAsync(
            new GetProjectStructureRequest("NonExistent"), CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Contains("not found", result.Error!.Message, StringComparison.OrdinalIgnoreCase);
    }

    // ─── Test 5: GetDependencyGraph_ReturnsTransitiveDependencies ───

    [Fact]
    public async Task GetDependencyGraph_ReturnsTransitiveDependencies()
    {
        // A -> B -> C (transitive chain)
        var helper = new WorkspaceTestHelper()
            .AddProject("C")
            .AddDocument("C", "C.cs", "namespace C { public class Base { } }")
            .AddProject("B", "C")
            .AddDocument("B", "B.cs", "namespace B { public class Mid { } }")
            .AddProject("A", "B")
            .AddDocument("A", "A.cs", "namespace A { public class Top { } }");

        var service = CreateService(helper);

        var result = await service.GetDependencyGraphAsync(
            new GetDependencyGraphRequest("A"), CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error?.Message);

        // Should include A, B, and C
        var nodeNames = result.Value!.Nodes.Select(n => n.ProjectName).ToHashSet();
        Assert.Contains("A", nodeNames);
        Assert.Contains("B", nodeNames);
        Assert.Contains("C", nodeNames);

        // A depends on B
        var nodeA = result.Value.Nodes.First(n => n.ProjectName == "A");
        Assert.Contains("B", nodeA.DependsOn);

        // B depends on C
        var nodeB = result.Value.Nodes.First(n => n.ProjectName == "B");
        Assert.Contains("C", nodeB.DependsOn);

        // C depends on nothing
        var nodeC = result.Value.Nodes.First(n => n.ProjectName == "C");
        Assert.Empty(nodeC.DependsOn);
    }

    // ─── Test 6: GetDependencyGraph_WithDepthLimit ───

    [Fact]
    public async Task GetDependencyGraph_WithDepthLimit()
    {
        // A -> B -> C, depth=1 should only return A and B
        var helper = new WorkspaceTestHelper()
            .AddProject("C")
            .AddDocument("C", "C.cs", "namespace C { public class Base { } }")
            .AddProject("B", "C")
            .AddDocument("B", "B.cs", "namespace B { public class Mid { } }")
            .AddProject("A", "B")
            .AddDocument("A", "A.cs", "namespace A { public class Top { } }");

        var service = CreateService(helper);

        var result = await service.GetDependencyGraphAsync(
            new GetDependencyGraphRequest("A", Depth: 1), CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error?.Message);

        var nodeNames = result.Value!.Nodes.Select(n => n.ProjectName).ToHashSet();
        Assert.Contains("A", nodeNames);
        Assert.Contains("B", nodeNames);
        // C should NOT be included (depth limit)
        Assert.DoesNotContain("C", nodeNames);
    }
}
