using RoslynMcp.Core.Tests.Helpers;
using RoslynMcp.Shared.Contracts.Refactor;
using Xunit;

namespace RoslynMcp.Core.Tests.Services;

public partial class RefactoringServiceTests
{
    [Fact]
    public async Task PreviewMoveType_ShowsSourceAndTarget()
    {
        var source = @"
namespace TestNs
{
    public class Widget
    {
        public void DoWork() { }
    }
}";
        var (service, filePath) = SetupService(source, "Widget.cs");
        var targetPath = WorkspaceTestHelper.GetFilePath("TestProject", "WidgetMoved.cs");

        var (widgetLine, widgetCol) = WorkspaceTestHelper.FindPosition(source, "class Widget");
        widgetCol += 6;
        var request = new MoveTypeRequest(filePath, Line: widgetLine, Column: widgetCol, TargetFilePath: targetPath);
        var result = await service.PreviewMoveTypeAsync(request, CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.NotNull(result.Value);
        Assert.NotNull(result.Value.Preview);
        Assert.Equal(2, result.Value.Preview.AffectedFiles.Count);
    }

    [Fact]
    public async Task ApplyMoveType_ReportsFilesChanged()
    {
        var source = @"
namespace TestNs
{
    public class Widget
    {
        public void DoWork() { }
    }
}";
        var (service, filePath) = SetupService(source, "Widget.cs");
        var targetPath = WorkspaceTestHelper.GetFilePath("TestProject", "WidgetMoved.cs");

        var (widgetLine, widgetCol) = WorkspaceTestHelper.FindPosition(source, "class Widget");
        widgetCol += 6;
        var request = new MoveTypeRequest(filePath, Line: widgetLine, Column: widgetCol, TargetFilePath: targetPath);
        var result = await service.ApplyMoveTypeAsync(request, CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.NotNull(result.Value);
        Assert.Equal(2, result.Value.FilesChanged);
        Assert.Equal(filePath, result.Value.SourceFilePath);
        Assert.Equal(targetPath, result.Value.TargetFilePath);
    }

    [Fact]
    public async Task PreviewMoveType_CancellationRethrows()
    {
        var source = @"
namespace TestNs
{
    public class Widget
    {
        public void DoWork() { }
    }
}";
        var (service, filePath) = SetupService(source, "Widget.cs");
        var targetPath = WorkspaceTestHelper.GetFilePath("TestProject", "WidgetMoved.cs");

        var (widgetLine, widgetCol) = WorkspaceTestHelper.FindPosition(source, "class Widget");
        widgetCol += 6;
        var request = new MoveTypeRequest(filePath, Line: widgetLine, Column: widgetCol, TargetFilePath: targetPath);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.PreviewMoveTypeAsync(request, cts.Token));
    }

    [Fact]
    public async Task PreviewMoveType_RejectsInvalidNamespace()
    {
        var source = @"
namespace TestNs
{
    public class Widget
    {
        public void DoWork() { }
    }
}";
        var (service, filePath) = SetupService(source, "Widget.cs");
        var targetPath = WorkspaceTestHelper.GetFilePath("TestProject", "WidgetMoved.cs");

        var (widgetLine, widgetCol) = WorkspaceTestHelper.FindPosition(source, "class Widget");
        widgetCol += 6;
        var request = new MoveTypeRequest(filePath, Line: widgetLine, Column: widgetCol,
            TargetFilePath: targetPath, TargetNamespace: "MyNs { } class Evil {}");
        var result = await service.PreviewMoveTypeAsync(request, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.NotNull(result.Error);
        Assert.Contains("identifier", result.Error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task MoveType_RejectsEmptyTargetFilePath()
    {
        var source = @"
namespace TestNs
{
    public class Widget
    {
        public void DoWork() { }
    }
}";
        var (service, filePath) = SetupService(source, "Widget.cs");

        var (widgetLine, widgetCol) = WorkspaceTestHelper.FindPosition(source, "class Widget");
        widgetCol += 6;
        var request = new MoveTypeRequest(filePath, Line: widgetLine, Column: widgetCol, TargetFilePath: "");
        var result = await service.PreviewMoveTypeAsync(request, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.Equal("INVALID_PATH", result.Error!.ErrorCode);
    }

    [Fact]
    public async Task ApplyMoveType_CancellationRethrows()
    {
        var source = @"
namespace TestNs
{
    public class Widget
    {
        public void DoWork() { }
    }
}";
        var (service, filePath) = SetupService(source, "Widget.cs");
        var targetPath = WorkspaceTestHelper.GetFilePath("TestProject", "WidgetMoved.cs");

        var (widgetLine, widgetCol) = WorkspaceTestHelper.FindPosition(source, "class Widget");
        widgetCol += 6;
        var request = new MoveTypeRequest(filePath, Line: widgetLine, Column: widgetCol, TargetFilePath: targetPath);

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.ApplyMoveTypeAsync(request, cts.Token));
    }
}
