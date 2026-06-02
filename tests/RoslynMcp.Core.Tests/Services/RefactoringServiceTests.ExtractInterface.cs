using RoslynMcp.Core.Tests.Helpers;
using RoslynMcp.Shared.Contracts.Refactor;
using Xunit;

namespace RoslynMcp.Core.Tests.Services;

public partial class RefactoringServiceTests
{
    [Fact]
    public async Task ExtractInterface_CreatesFromPublicMembers()
    {
        var source = @"
namespace TestNs
{
    public class MyService
    {
        public void Execute() { }
        public string Name { get; set; }
        private void InternalWork() { }
    }
}";
        var (service, filePath) = SetupService(source, "MyService.cs");

        var (svcLine, svcCol) = WorkspaceTestHelper.FindPosition(source, "MyService");
        var request = new ExtractInterfaceRequest(filePath, Line: svcLine, Column: svcCol, InterfaceName: "IMyService");
        var result = await service.PreviewExtractInterfaceAsync(request, CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.NotNull(result.Value);
        Assert.Equal("IMyService", result.Value.InterfaceName);
        Assert.Contains("Execute", result.Value.ExtractedMembers);
        Assert.Contains("Name", result.Value.ExtractedMembers);
        Assert.DoesNotContain("InternalWork", result.Value.ExtractedMembers);
    }

    [Fact]
    public async Task ExtractInterface_FiltersByMemberNames()
    {
        var source = @"
namespace TestNs
{
    public class MyService
    {
        public void Execute() { }
        public string Name { get; set; }
        private void InternalWork() { }
    }
}";
        var (service, filePath) = SetupService(source, "MyService.cs");

        var (svcLine, svcCol) = WorkspaceTestHelper.FindPosition(source, "MyService");
        var request = new ExtractInterfaceRequest(
            filePath, Line: svcLine, Column: svcCol,
            InterfaceName: "IMyService",
            MemberNames: new[] { "Execute" });
        var result = await service.PreviewExtractInterfaceAsync(request, CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.NotNull(result.Value);
        Assert.Single(result.Value.ExtractedMembers);
        Assert.Contains("Execute", result.Value.ExtractedMembers);
        Assert.DoesNotContain("Name", result.Value.ExtractedMembers);
    }

    [Fact]
    public async Task ExtractInterface_CancellationRethrows()
    {
        var source = @"
namespace TestNs
{
    public class MyService
    {
        public void Execute() { }
    }
}";
        var (service, filePath) = SetupService(source, "MyService.cs");

        var (svcLine, svcCol) = WorkspaceTestHelper.FindPosition(source, "MyService");
        var request = new ExtractInterfaceRequest(filePath, Line: svcLine, Column: svcCol, InterfaceName: "IMyService");

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.PreviewExtractInterfaceAsync(request, cts.Token));
    }

    [Fact]
    public async Task ExtractInterface_RejectsKeywordAsInterfaceName()
    {
        var source = @"
namespace TestNs
{
    public class MyService
    {
        public void Execute() { }
    }
}";
        var (service, filePath) = SetupService(source, "MyService.cs");
        var (line, col) = WorkspaceTestHelper.FindPosition(source, "MyService");

        var request = new ExtractInterfaceRequest(filePath, Line: line, Column: col, InterfaceName: "class");
        var result = await service.PreviewExtractInterfaceAsync(request, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.NotNull(result.Error);
        Assert.Equal("RESERVED_KEYWORD", result.Error!.ErrorCode);
    }

    [Fact]
    public async Task ExtractInterface_RejectsInvalidIdentifier()
    {
        var source = @"
namespace TestNs
{
    public class MyService
    {
        public void Execute() { }
    }
}";
        var (service, filePath) = SetupService(source, "MyService.cs");
        var (line, col) = WorkspaceTestHelper.FindPosition(source, "MyService");

        var request = new ExtractInterfaceRequest(filePath, Line: line, Column: col, InterfaceName: "../../evil");
        var result = await service.PreviewExtractInterfaceAsync(request, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.NotNull(result.Error);
        Assert.Equal("INVALID_IDENTIFIER", result.Error!.ErrorCode);
    }

    [Fact]
    public async Task ExtractInterface_RejectsTraversalInTargetFilePath()
    {
        var source = @"
namespace TestNs
{
    public class MyService
    {
        public void Execute() { }
    }
}";
        var helper = new WorkspaceTestHelper()
            .WithSolutionPath(@"C:\test\TestProject.sln")
            .AddProject("TestProject")
            .AddDocument("TestProject", "MyService.cs", source);
        var service = CreateService(helper);
        var filePath = WorkspaceTestHelper.GetFilePath("TestProject", "MyService.cs");

        var (line, col) = WorkspaceTestHelper.FindPosition(source, "MyService");
        var request = new ExtractInterfaceRequest(
            filePath, Line: line, Column: col,
            InterfaceName: "IMyService",
            TargetFilePath: @"C:\test\..\evil.cs");
        var result = await service.PreviewExtractInterfaceAsync(request, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("PATH_OUTSIDE_SOLUTION", result.Error!.ErrorCode);
    }

    [Fact]
    public async Task ExtractInterface_Apply_ReturnsFilesChanged()
    {
        var source = @"
namespace TestNs
{
    public class MyService
    {
        public void Execute() { }
        public string Name { get; set; }
        private void InternalWork() { }
    }
}";
        var (service, filePath) = SetupService(source, "MyService.cs");
        var (svcLine, svcCol) = WorkspaceTestHelper.FindPosition(source, "MyService");
        var request = new ExtractInterfaceRequest(filePath, Line: svcLine, Column: svcCol, InterfaceName: "IMyService");
        var result = await service.ApplyExtractInterfaceAsync(request, CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.NotNull(result.Value);
        Assert.Equal(2, result.Value.FilesChanged);
        Assert.Contains("Execute", result.Value.ExtractedMembers);
    }
}
