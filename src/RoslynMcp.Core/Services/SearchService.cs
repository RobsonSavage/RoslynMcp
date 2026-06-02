using Microsoft.CodeAnalysis;
using RoslynMcp.Core.Helpers;
using RoslynMcp.Shared;
using Serilog;

namespace RoslynMcp.Core.Services;

public partial class SearchService
{
    private readonly IWorkspaceProvider _workspace;
    private readonly IWorkspaceHelpers _helpers;
    private readonly ILogger _logger;
    public SearchService(IWorkspaceProvider workspace, IWorkspaceHelpers helpers, ILogger logger)
    {
        _workspace = workspace;
        _helpers = helpers;
        _logger = logger;
    }
}
