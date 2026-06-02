using RoslynMcp.Core.Services;
using RoslynMcp.Core.Tests.Helpers;
using RoslynMcp.Shared.Contracts.Apollo;
using Serilog;
using Xunit;

namespace RoslynMcp.Core.Tests.Services;

public class ApolloServiceTests : IDisposable
{
    private readonly ILogger _logger = new LoggerConfiguration().CreateLogger();
    private WorkspaceTestHelper? _helper;

    private ApolloService CreateService(WorkspaceTestHelper helper)
    {
        _helper = helper;
        var provider = helper.CreateProvider();
        return new ApolloService(provider, _logger);
    }

    public void Dispose() => _helper?.Dispose();

    // ── Source constants ──

    const string ErrorCode = @"
namespace TestNs
{
    public class Broken
    {
        public void Method()
        {
            int x = ""hello"";  // CS0029: Cannot implicitly convert type 'string' to 'int'
        }
    }
}";

    const string MultiErrorCode = @"
namespace TestNs
{
    public class Broken
    {
        public void Method()
        {
            int x = ""hello"";  // CS0029
            unknownVar = 5;     // CS0103
        }
    }
}";

    const string ValidCode = @"
namespace TestNs
{
    public class Working
    {
        public int GetValue() => 42;
    }
}";

    // ────── 1. Diagnose_FindsErrors_InFile ──────

    [Fact]
    public async Task Diagnose_FindsErrors_InFile()
    {
        var filePath = WorkspaceTestHelper.GetFilePath("TestProject", "Error.cs");
        var helper = new WorkspaceTestHelper()
            .AddProject("TestProject")
            .AddDocument("TestProject", "Error.cs", ErrorCode);

        var service = CreateService(helper);

        var request = new ApolloDiagnoseRequest(FilePath: filePath);
        var result = await service.DiagnoseAsync(request, CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error?.Message);
        var response = result.Value!;
        Assert.NotEmpty(response.Diagnostics);
        Assert.Contains(response.Diagnostics, d => d.Id == "CS0029");
        Assert.NotNull(response.RootCause);
    }

    // ────── 2. Diagnose_FiltersByErrorId ──────

    [Fact]
    public async Task Diagnose_FiltersByErrorId()
    {
        var filePath = WorkspaceTestHelper.GetFilePath("TestProject", "MultiError.cs");
        var helper = new WorkspaceTestHelper()
            .AddProject("TestProject")
            .AddDocument("TestProject", "MultiError.cs", MultiErrorCode);

        var service = CreateService(helper);

        var request = new ApolloDiagnoseRequest(FilePath: filePath, ErrorId: "CS0029");
        var result = await service.DiagnoseAsync(request, CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error?.Message);
        var response = result.Value!;
        Assert.NotEmpty(response.Diagnostics);
        Assert.All(response.Diagnostics, d => Assert.Equal("CS0029", d.Id));
    }

    // ────── 3. Diagnose_NoErrors_ReturnsEmpty ──────

    [Fact]
    public async Task Diagnose_NoErrors_ReturnsEmpty()
    {
        var filePath = WorkspaceTestHelper.GetFilePath("TestProject", "Valid.cs");
        var helper = new WorkspaceTestHelper()
            .AddProject("TestProject")
            .AddDocument("TestProject", "Valid.cs", ValidCode);

        var service = CreateService(helper);

        var request = new ApolloDiagnoseRequest(FilePath: filePath);
        var result = await service.DiagnoseAsync(request, CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error?.Message);
        var response = result.Value!;
        Assert.Empty(response.Diagnostics);
        Assert.Null(response.RootCause);
    }

    // ────── 4. Isolate_FindsErrorLocation ──────

    [Fact]
    public async Task Isolate_FindsErrorLocation()
    {
        var filePath = WorkspaceTestHelper.GetFilePath("TestProject", "Error.cs");
        var helper = new WorkspaceTestHelper()
            .AddProject("TestProject")
            .AddDocument("TestProject", "Error.cs", ErrorCode);

        var service = CreateService(helper);

        var request = new ApolloIsolateRequest(FilePath: filePath);
        var result = await service.IsolateAsync(request, CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error?.Message);
        var response = result.Value!;
        Assert.NotNull(response.IsolatedRange);
        Assert.NotNull(response.SuspectedCause);
        Assert.True(
            response.Confidence == "high" || response.Confidence == "medium",
            $"Expected confidence 'high' or 'medium', got '{response.Confidence}'");
    }

    // ────── 5. Isolate_NoMatchingError_ReturnsNone ──────

    [Fact]
    public async Task Isolate_NoMatchingError_ReturnsNone()
    {
        var filePath = WorkspaceTestHelper.GetFilePath("TestProject", "Valid.cs");
        var helper = new WorkspaceTestHelper()
            .AddProject("TestProject")
            .AddDocument("TestProject", "Valid.cs", ValidCode);

        var service = CreateService(helper);

        var request = new ApolloIsolateRequest(FilePath: filePath, ErrorId: "CS9999");
        var result = await service.IsolateAsync(request, CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error?.Message);
        var response = result.Value!;
        Assert.Contains("No matching errors found", response.SuspectedCause);
        Assert.Equal("none", response.Confidence);
    }

    // ────── 6. Fix_ReturnsPreviewChanges ──────

    [Fact]
    public async Task Fix_ReturnsPreviewChanges()
    {
        // CS0103: The name 'unknownVar' does not exist in the current context
        const string cs0103Code = @"
namespace TestNs
{
    public class Broken
    {
        public void Method()
        {
            unknownVar = 5;
        }
    }
}";
        var filePath = WorkspaceTestHelper.GetFilePath("TestProject", "Undefined.cs");
        var helper = new WorkspaceTestHelper()
            .AddProject("TestProject")
            .AddDocument("TestProject", "Undefined.cs", cs0103Code);

        var service = CreateService(helper);

        var request = new ApolloFixRequest(
            FilePath: filePath,
            DiagnosticId: "CS0103",
            Preview: true);
        var result = await service.FixAsync(request, CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error?.Message);
        var response = result.Value!;
        Assert.False(response.Applied);
    }

    // ────── 7. Validate_WithErrors_NotResolved ──────

    [Fact]
    public async Task Validate_WithErrors_NotResolved()
    {
        var filePath = WorkspaceTestHelper.GetFilePath("TestProject", "Error.cs");
        var helper = new WorkspaceTestHelper()
            .AddProject("TestProject")
            .AddDocument("TestProject", "Error.cs", ErrorCode);

        var service = CreateService(helper);

        var request = new ApolloValidateRequest(FilePath: filePath);
        var result = await service.ValidateAsync(request, CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error?.Message);
        var response = result.Value!;
        Assert.False(response.Resolved);
        Assert.NotEmpty(response.RemainingErrors);
    }

    // ────── 8. Validate_CleanFile_Resolved ──────

    [Fact]
    public async Task Validate_CleanFile_Resolved()
    {
        var filePath = WorkspaceTestHelper.GetFilePath("TestProject", "Valid.cs");
        var helper = new WorkspaceTestHelper()
            .AddProject("TestProject")
            .AddDocument("TestProject", "Valid.cs", ValidCode);

        var service = CreateService(helper);

        var request = new ApolloValidateRequest(
            FilePath: filePath,
            OriginalDiagnosticId: "CS0029");
        var result = await service.ValidateAsync(request, CancellationToken.None);

        Assert.True(result.IsSuccess, result.Error?.Message);
        var response = result.Value!;
        Assert.True(response.Resolved);
    }
}
