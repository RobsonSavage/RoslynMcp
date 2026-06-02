using RoslynMcp.Core.Tests.Helpers;
using RoslynMcp.Shared.Contracts.Refactor;
using Xunit;

namespace RoslynMcp.Core.Tests.Services;

public partial class RefactoringServiceTests
{
    [Fact]
    public async Task OrganizeUsings_RemovesUnused()
    {
        var source = @"using System;
using System.Collections.Generic;

namespace TestNs
{
    public class Foo
    {
        public void Run() { System.Console.WriteLine(""hello""); }
    }
}";
        var filePath = WorkspaceTestHelper.GetFilePath("TestProject", "Foo.cs");
        var helper = new WorkspaceTestHelper()
            .AddProject("TestProject")
            .AddDocument("TestProject", "Foo.cs", source);

        var service = CreateService(helper);

        var request = new OrganizeUsingsRequest(filePath, RemoveUnused: true, Sort: false);
        var result = await service.OrganizeUsingsAsync(request, CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.True(result.Value!.UsingsRemoved >= 1,
            $"Expected UsingsRemoved >= 1, got {result.Value.UsingsRemoved}");
        Assert.Contains(result.Value.RemovedUsings, u => u.Contains("System.Collections.Generic"));
    }

    [Fact]
    public async Task OrganizeUsings_SortsByConvention()
    {
        var source = @"using Foo;
using System;

namespace TestNs
{
    public class Bar { }
}";
        var filePath = WorkspaceTestHelper.GetFilePath("TestProject", "Bar.cs");
        var helper = new WorkspaceTestHelper()
            .AddProject("TestProject")
            .AddDocument("TestProject", "Bar.cs", source);

        var service = CreateService(helper);

        var request = new OrganizeUsingsRequest(filePath, RemoveUnused: false, Sort: true);
        var result = await service.OrganizeUsingsAsync(request, CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.True(result.Value!.UsingsSorted > 0,
            $"Expected UsingsSorted > 0, got {result.Value.UsingsSorted}");
    }

    [Fact]
    public async Task OrganizeUsings_CancellationRethrows()
    {
        var source = @"using System;
using System.Collections.Generic;

namespace TestNs
{
    public class Foo { }
}";
        var filePath = WorkspaceTestHelper.GetFilePath("TestProject", "Foo.cs");
        var helper = new WorkspaceTestHelper()
            .AddProject("TestProject")
            .AddDocument("TestProject", "Foo.cs", source);

        var service = CreateService(helper);

        var request = new OrganizeUsingsRequest(filePath, RemoveUnused: true, Sort: false);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.OrganizeUsingsAsync(request, cts.Token));
    }
}
