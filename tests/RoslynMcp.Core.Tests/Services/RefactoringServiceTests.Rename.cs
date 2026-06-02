using RoslynMcp.Core.Tests.Helpers;
using RoslynMcp.Shared.Contracts.Refactor;
using Xunit;

namespace RoslynMcp.Core.Tests.Services;

public partial class RefactoringServiceTests
{
    [Fact]
    public async Task PreviewRename_ShowsAffectedFiles()
    {
        var source = @"
namespace TestNs
{
    public class Foo
    {
        public void Bar() { }
        public void Caller() { Bar(); }
    }
}";
        var (service, filePath) = SetupService(source, "Foo.cs");

        var (barLine, barCol) = WorkspaceTestHelper.FindPosition(source, "void Bar");
        barCol += 5;
        var request = new RenameRequest(filePath, Line: barLine, Column: barCol, NewName: "Baz");
        var result = await service.PreviewRenameAsync(request, CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.NotNull(result.Value);
        Assert.NotNull(result.Value.Preview);
        Assert.True(result.Value.Preview.AffectedFiles.Count >= 1,
            $"Expected at least 1 affected file, got {result.Value.Preview.AffectedFiles.Count}");
        Assert.True(result.Value.Preview.TotalChanges > 0,
            $"Expected TotalChanges > 0, got {result.Value.Preview.TotalChanges}");

        Assert.Equal("Baz", result.Value.NewName);
        var affectedFile = result.Value.Preview.AffectedFiles.Single(f => f.FilePath == filePath);
        Assert.True(affectedFile.Changes.Count >= 1, "Expected at least 1 text change in affected file");
        Assert.Contains(affectedFile.Changes, c => c.NewText.Contains("Baz"));
    }

    [Fact]
    public async Task ApplyRename_ReturnsChangeCounts()
    {
        var source = @"
namespace TestNs
{
    public class Foo
    {
        public void Bar() { }
        public void Caller() { Bar(); }
    }
}";
        var (service, filePath) = SetupService(source, "Foo.cs");

        var (barLine, barCol) = WorkspaceTestHelper.FindPosition(source, "void Bar");
        barCol += 5;
        var request = new RenameRequest(filePath, Line: barLine, Column: barCol, NewName: "Baz");
        var result = await service.ApplyRenameAsync(request, CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.NotNull(result.Value);
        Assert.True(result.Value.FilesChanged >= 1,
            $"Expected FilesChanged >= 1, got {result.Value.FilesChanged}");
        Assert.True(result.Value.TotalReplacements >= 1,
            $"Expected TotalReplacements >= 1, got {result.Value.TotalReplacements}");

        Assert.Equal("Baz", result.Value.NewName);
        Assert.Equal("Bar", result.Value.Symbol.Name);
    }

    [Fact]
    public async Task PreviewRename_FailsOnInvalidPosition()
    {
        var source = @"
namespace TestNs
{
    public class Foo
    {
    }
}";
        var (service, filePath) = SetupService(source, "Foo.cs");

        var request = new RenameRequest(filePath, Line: 100, Column: 0, NewName: "NewName");
        var result = await service.PreviewRenameAsync(request, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public async Task PreviewRename_CancellationRethrows()
    {
        var source = @"
namespace TestNs
{
    public class Foo
    {
        public void Bar() { }
    }
}";
        var (service, filePath) = SetupService(source, "Foo.cs");

        var (barLine, barCol) = WorkspaceTestHelper.FindPosition(source, "void Bar");
        barCol += 5;
        var request = new RenameRequest(filePath, Line: barLine, Column: barCol, NewName: "Baz");

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.PreviewRenameAsync(request, cts.Token));
    }

    [Fact]
    public async Task PreviewRename_RejectsKeywordAsNewName()
    {
        var source = @"
namespace TestNs
{
    public class Foo
    {
        public void Bar() { }
    }
}";
        var (service, filePath) = SetupService(source, "Foo.cs");

        var (barLine, barCol) = WorkspaceTestHelper.FindPosition(source, "void Bar");
        barCol += 5;
        var request = new RenameRequest(filePath, Line: barLine, Column: barCol, NewName: "public");
        var result = await service.PreviewRenameAsync(request, CancellationToken.None);

        Assert.False(result.IsSuccess);
        Assert.NotNull(result.Error);
        Assert.Contains("keyword", result.Error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ApplyRename_CancellationRethrows()
    {
        var source = @"
namespace TestNs
{
    public class Foo
    {
        public void Bar() { }
        public void Caller() { Bar(); }
    }
}";
        var (service, filePath) = SetupService(source, "Foo.cs");

        var (barLine, barCol) = WorkspaceTestHelper.FindPosition(source, "void Bar");
        barCol += 5;
        var request = new RenameRequest(filePath, Line: barLine, Column: barCol, NewName: "Baz");

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => service.ApplyRenameAsync(request, cts.Token));
    }
}
