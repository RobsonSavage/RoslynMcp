using RoslynMcp.Core.Helpers;
using RoslynMcp.Shared;
using Serilog;

namespace RoslynMcp.Core.Services;

public partial class StructureService
{
    private readonly IWorkspaceProvider _workspace;
    private readonly IWorkspaceHelpers _helpers;
    private readonly ILogger _logger;

    public StructureService(IWorkspaceProvider workspace, IWorkspaceHelpers helpers, ILogger logger)
    {
        _workspace = workspace;
        _helpers = helpers;
        _logger = logger;
    }
}
