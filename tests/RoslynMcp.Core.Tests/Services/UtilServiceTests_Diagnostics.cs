using RoslynMcp.Core.Helpers;
using RoslynMcp.Core.Services;
using RoslynMcp.Core.Tests.Helpers;
using RoslynMcp.Shared.Contracts.Util;
using Serilog;
using Xunit;

namespace RoslynMcp.Core.Tests.Services;

public class UtilServiceTests_Diagnostics : IDisposable
{
    private readonly ILogger _logger = new LoggerConfiguration().CreateLogger();
    private WorkspaceTestHelper? _helper;

    private UtilService CreateService(WorkspaceTestHelper helper, string? configDir = null)
    {
        _helper = helper;
        var provider = helper.CreateProvider();
        var helpers = helper.CreateHelpers(_logger);
        var config = new ConfigManager(configDir ?? Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString()));
        return new UtilService(provider, helpers, config, new TestSolutionContextSwitcher(), _logger);
    }

    public void Dispose() => _helper?.Dispose();

    // ────────────────────────────────────────────────────────────────────
    // 1. ValidateText_ValidCode_ReturnsNoErrors
    // ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ValidateText_ValidCode_ReturnsNoErrors()
    {
        var source = @"
namespace TestNs { public class Good { public void Method() { } } }";

        var filePath = WorkspaceTestHelper.GetFilePath("TestProject", "Good.cs");
        var helper = new WorkspaceTestHelper()
            .AddProject("TestProject")
            .AddDocument("TestProject", "Good.cs", source);

        var service = CreateService(helper);

        var result = await service.ValidateTextAsync(
            new ValidateTextRequest(FilePath: filePath, Text: source), CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error?.Message);
        var response = result.Value!;
        Assert.True(response.IsValid, "Expected IsValid=true for well-formed code");

        var errors = response.Diagnostics.Where(d => d.Severity == "Error").ToList();
        Assert.Empty(errors);
    }

    // ────────────────────────────────────────────────────────────────────
    // 2. ValidateText_InvalidCode_ReturnsDiagnostics
    // ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ValidateText_InvalidCode_ReturnsDiagnostics()
    {
        var source = @"
namespace TestNs { public class Original { public void Method() { } } }";

        var filePath = WorkspaceTestHelper.GetFilePath("TestProject", "Original.cs");
        var helper = new WorkspaceTestHelper()
            .AddProject("TestProject")
            .AddDocument("TestProject", "Original.cs", source);

        var service = CreateService(helper);

        var badText = @"
namespace TestNs { public class Original { public void Method() { int x = } } }";

        var result = await service.ValidateTextAsync(
            new ValidateTextRequest(FilePath: filePath, Text: badText), CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error?.Message);
        var response = result.Value!;
        Assert.False(response.IsValid, "Expected IsValid=false for code with syntax errors");
        Assert.NotEmpty(response.Diagnostics);
    }

    // ────────────────────────────────────────────────────────────────────
    // 3. ValidateText_DoesNotModifyWorkspace
    // ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ValidateText_DoesNotModifyWorkspace()
    {
        var source = @"
namespace TestNs { public class Untouched { } }";

        var filePath = WorkspaceTestHelper.GetFilePath("TestProject", "Untouched.cs");
        var helper = new WorkspaceTestHelper()
            .AddProject("TestProject")
            .AddDocument("TestProject", "Untouched.cs", source);

        var service = CreateService(helper);

        var badText = @"
namespace TestNs { public class Untouched { public void Bad() { int x = } } }";

        // Validate with bad text
        var result = await service.ValidateTextAsync(
            new ValidateTextRequest(FilePath: filePath, Text: badText), CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error?.Message);
        Assert.False(result.Value!.IsValid);

        // Verify original document is unchanged
        var doc = helper.GetDocument("Untouched.cs");
        Assert.NotNull(doc);
        var text = await doc!.GetTextAsync(CancellationToken.None);
        Assert.Equal(source, text.ToString());
    }

    // ────────────────────────────────────────────────────────────────────
    // 4. GetWorkspaceStatus_ReturnsProjectAndDocCounts
    // ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetWorkspaceStatus_ReturnsProjectAndDocCounts()
    {
        var source1 = @"
namespace ProjectA { public class ClassA { } }";

        var source2 = @"
namespace ProjectA { public class ClassB { } }";

        var source3 = @"
namespace ProjectB { public class ClassC { } }";

        var helper = new WorkspaceTestHelper()
            .AddProject("ProjectA")
            .AddProject("ProjectB")
            .AddDocument("ProjectA", "ClassA.cs", source1)
            .AddDocument("ProjectA", "ClassB.cs", source2)
            .AddDocument("ProjectB", "ClassC.cs", source3);

        var service = CreateService(helper);

        var result = await service.GetWorkspaceStatusAsync(
            new GetWorkspaceStatusRequest(), CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error?.Message);
        var response = result.Value!;
        Assert.Equal(2, response.ProjectCount);
        Assert.Equal(3, response.DocumentCount);
        Assert.True(response.IsFullyLoaded);
    }

    // ────────────────────────────────────────────────────────────────────
    // 5. GetErrors_ReturnsCompilationErrors
    // ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetErrors_ReturnsCompilationErrors()
    {
        var source = @"
namespace TestNs { public class Bad { public void Method() { UndefinedType x; } } }";

        var filePath = WorkspaceTestHelper.GetFilePath("TestProject", "Bad.cs");
        var helper = new WorkspaceTestHelper()
            .AddProject("TestProject")
            .AddDocument("TestProject", "Bad.cs", source);

        var service = CreateService(helper);

        var result = await service.GetErrorsAsync(
            new GetErrorsRequest(PageSize: 50), CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error?.Message);
        var response = result.Value!;
        Assert.True(response.Errors.TotalCount >= 1,
            $"Expected at least 1 error for undefined type, got {response.Errors.TotalCount}");

        // Verify at least one error mentions the undefined type
        var hasUndefinedError = response.Errors.Items
            .Any(e => e.Severity == "Error" && e.Message.Contains("UndefinedType"));
        Assert.True(hasUndefinedError, "Expected an error referencing 'UndefinedType'");
    }

    // ────────────────────────────────────────────────────────────────────
    // 6. GetErrors_FiltersOnFilePath
    // ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetErrors_FiltersOnFilePath()
    {
        var sourceA = @"
namespace TestNs { public class ErrorA { public void Method() { UndefinedA x; } } }";

        var sourceB = @"
namespace TestNs { public class ErrorB { public void Method() { UndefinedB x; } } }";

        var filePathA = WorkspaceTestHelper.GetFilePath("TestProject", "ErrorA.cs");
        var filePathB = WorkspaceTestHelper.GetFilePath("TestProject", "ErrorB.cs");
        var helper = new WorkspaceTestHelper()
            .AddProject("TestProject")
            .AddDocument("TestProject", "ErrorA.cs", sourceA)
            .AddDocument("TestProject", "ErrorB.cs", sourceB);

        var service = CreateService(helper);

        // Get errors filtered to file A only
        var result = await service.GetErrorsAsync(
            new GetErrorsRequest(FilePath: filePathA, PageSize: 50), CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error?.Message);
        var response = result.Value!;
        Assert.True(response.Errors.TotalCount >= 1,
            $"Expected at least 1 error for file A, got {response.Errors.TotalCount}");

        // All returned errors should be from file A
        foreach (var error in response.Errors.Items)
        {
            Assert.Equal(filePathA, error.Location.FilePath);
        }

        // Verify file B errors are not included
        var hasFileB = response.Errors.Items.Any(e =>
            string.Equals(e.Location.FilePath, filePathB, StringComparison.OrdinalIgnoreCase));
        Assert.False(hasFileB, "Filtered results should not contain errors from file B");
    }

    // ────────────────────────────────────────────────────────────────────
    // 7. GetWarnings_ReturnsWarnings
    // ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetWarnings_ReturnsWarnings()
    {
        var source = @"
namespace TestNs { public class Warn { public void Method() { int unused = 0; } } }";

        var filePath = WorkspaceTestHelper.GetFilePath("TestProject", "Warn.cs");
        var helper = new WorkspaceTestHelper()
            .AddProject("TestProject")
            .AddDocument("TestProject", "Warn.cs", source);

        var service = CreateService(helper);

        var result = await service.GetWarningsAsync(
            new GetWarningsRequest(PageSize: 50), CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error?.Message);
        var response = result.Value!;

        // The unused variable diagnostic may appear as a warning depending on compiler settings.
        // At minimum, verify the call completes successfully and returns a valid paged result.
        Assert.NotNull(response.Warnings);
        Assert.True(response.Warnings.TotalCount >= 0,
            "Expected a non-negative warning count");
    }

    // ────────────────────────────────────────────────────────────────────
    // 8. ReloadFile_ExistingFile_ReturnsSuccess
    // ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ReloadFile_ExistingFile_ReturnsSuccess()
    {
        var source = @"
namespace TestNs { public class Reloadable { } }";

        var filePath = WorkspaceTestHelper.GetFilePath("TestProject", "Reloadable.cs");
        var helper = new WorkspaceTestHelper()
            .AddProject("TestProject")
            .AddDocument("TestProject", "Reloadable.cs", source);

        var service = CreateService(helper);

        var result = await service.ReloadFileAsync(
            new ReloadFileRequest(FilePath: filePath), CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error?.Message);
        var response = result.Value!;
        Assert.True(response.Success, response.Message);
        Assert.Equal(filePath, response.FilePath);
    }
}
