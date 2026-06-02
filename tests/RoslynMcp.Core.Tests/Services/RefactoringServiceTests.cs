using RoslynMcp.Core.Helpers;
using RoslynMcp.Core.Services;
using RoslynMcp.Core.Tests.Helpers;
using Serilog;

namespace RoslynMcp.Core.Tests.Services;

public partial class RefactoringServiceTests : IDisposable
{
    private readonly ILogger _logger = Serilog.Core.Logger.None;
    private WorkspaceTestHelper? _helper;

    protected RefactoringService CreateService(WorkspaceTestHelper helper)
    {
        _helper?.Dispose();
        _helper = helper;
        var provider = helper.CreateProvider();
        var helpers = helper.CreateHelpers(_logger);
        return new RefactoringService(provider, helpers, _logger);
    }

    protected (RefactoringService service, string filePath) SetupService(string source, string fileName = "TestFile.cs", string projectName = "TestProject")
    {
        var filePath = WorkspaceTestHelper.GetFilePath(projectName, fileName);
        var helper = new WorkspaceTestHelper()
            .AddProject(projectName)
            .AddDocument(projectName, fileName, source);
        return (CreateService(helper), filePath);
    }

    public void Dispose()
    {
        _helper?.Dispose();
    }
}
