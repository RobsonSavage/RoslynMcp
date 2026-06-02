using RoslynMcp.Core.Helpers;
using RoslynMcp.Shared;
using Serilog;

namespace RoslynMcp.Core.Services;

public partial class GraphService
{
    private readonly ISqliteConnectionPool _pool;
    private readonly IWorkspaceProvider? _workspace;
    private readonly ILogger _logger;
    private readonly int _maxBfsNodes;
    private long _mutationVersion = 1;    // Graph starts stale (never rebuilt)
    private long _rebuiltVersion;          // No rebuild yet

    private bool IsStale
    {
        get
        {
            long rebuilt = Interlocked.Read(ref _rebuiltVersion);
            long mutation = Interlocked.Read(ref _mutationVersion);
            return mutation != rebuilt;
        }
    }

    public GraphService(ISqliteConnectionPool pool, ILogger logger, IWorkspaceProvider? workspace = null, int? maxBfsNodes = null)
    {
        _pool = pool;
        _logger = logger;
        _workspace = workspace;
        _maxBfsNodes = maxBfsNodes ?? ValidationLimits.MaxBfsNodes;
    }
}
