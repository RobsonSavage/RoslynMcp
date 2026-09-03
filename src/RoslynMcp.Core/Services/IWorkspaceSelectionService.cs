using RoslynMcp.Shared;
using RoslynMcp.Shared.Contracts.Util;

namespace RoslynMcp.Core.Services;

public interface IWorkspaceSelectionService
{
    Task<Result<SetSolutionPathResponse>> SetSolutionPathAsync(
        SetSolutionPathRequest request,
        CancellationToken ct = default);

    Task<Result<SetSolutionRootResponse>> SetSolutionRootAsync(
        SetSolutionRootRequest request,
        CancellationToken ct = default);
}
