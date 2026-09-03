using RoslynMcp.Shared.Contracts.Util;

namespace RoslynMcp.Core.Services;

public interface ISolutionContextSwitcher
{
    Task<SetSolutionPathResponse> SwitchAsync(
        string solutionPath,
        bool warmUp,
        CancellationToken ct = default);
}
