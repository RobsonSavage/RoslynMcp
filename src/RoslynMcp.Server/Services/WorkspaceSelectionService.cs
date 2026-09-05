using RoslynMcp.Core.Services;
using RoslynMcp.Shared;
using RoslynMcp.Shared.Contracts.Util;
using Serilog;

namespace RoslynMcp.Server.Services;

internal sealed class WorkspaceSelectionService : IWorkspaceSelectionService, IDisposable
{
    private readonly IWorkspaceProvider _workspace;
    private readonly ISolutionContextSwitcher _switcher;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public WorkspaceSelectionService(
        IWorkspaceProvider workspace,
        ISolutionContextSwitcher switcher,
        ILogger logger)
    {
        _workspace = workspace;
        _switcher = switcher;
        _logger = logger;
    }

    public async Task<Result<SetSolutionPathResponse>> SetSolutionPathAsync(
        SetSolutionPathRequest request,
        CancellationToken ct = default)
    {
        var validation = ValidateSolutionPath(request.SolutionPath);
        if (!validation.IsSuccess)
            return Result<SetSolutionPathResponse>.Fail(validation.Error!.Message);

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var response = await _switcher.SwitchAsync(validation.Value!, request.WarmUp, ct).ConfigureAwait(false);
            return Result<SetSolutionPathResponse>.Ok(response);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<Result<SetSolutionRootResponse>> SetSolutionRootAsync(
        SetSolutionRootRequest request,
        CancellationToken ct = default)
    {
        string fullRootPath;
        try
        {
            fullRootPath = Path.GetFullPath(request.RootPath);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            _logger.Warning(ex, "Invalid workspace root path {RootPath}", request.RootPath);
            return Result<SetSolutionRootResponse>.Fail($"Invalid root path: {ex.Message}");
        }

        if (!Directory.Exists(fullRootPath))
        {
            _logger.Warning("Workspace root does not exist: {RootPath}", fullRootPath);
            return Result<SetSolutionRootResponse>.Fail($"Workspace root not found: {fullRootPath}");
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            string? solutionPath;
            try
            {
                solutionPath = SolutionDiscovery.Discover(fullRootPath, _logger);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "Could not discover a solution from workspace root {RootPath}", fullRootPath);
                return Result<SetSolutionRootResponse>.Fail(
                    $"Could not discover a solution from workspace root: {fullRootPath}");
            }

            if (solutionPath == null)
                return Result<SetSolutionRootResponse>.Fail(
                    $"No .sln or .slnx found in the git repository containing: {fullRootPath}");

            var currentPath = _workspace.SolutionPath;
            if (PathsEqual(currentPath, solutionPath))
                return CurrentResponse(fullRootPath);

            try
            {
                var switched = await _switcher.SwitchAsync(solutionPath, request.WarmUp, ct).ConfigureAwait(false);
                return Result<SetSolutionRootResponse>.Ok(new SetSolutionRootResponse(
                    fullRootPath,
                    switched.SolutionPath,
                    switched.ProjectCount,
                    switched.DocumentCount,
                    Changed: true,
                    switched.PreviousSolutionPath));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.Warning(ex, "Failed to switch to solution discovered from workspace root {RootPath}", fullRootPath);
                return Result<SetSolutionRootResponse>.Fail(
                    $"Failed to load solution discovered from workspace root: {solutionPath}");
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose() => _gate.Dispose();

    private Result<SetSolutionRootResponse> CurrentResponse(string rootPath)
    {
        var solution = _workspace.CurrentSolution;
        var solutionPath = _workspace.SolutionPath ?? string.Empty;
        return Result<SetSolutionRootResponse>.Ok(new SetSolutionRootResponse(
            rootPath,
            solutionPath,
            solution?.ProjectIds.Count ?? 0,
            solution?.Projects.Sum(project => project.DocumentIds.Count) ?? 0,
            Changed: false));
    }

    private static Result<string> ValidateSolutionPath(string solutionPath)
    {
        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(solutionPath);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return Result<string>.Fail($"Invalid solution path: {ex.Message}");
        }

        if (!File.Exists(fullPath))
            return Result<string>.Fail($"Solution file not found: {fullPath}");

        var extension = Path.GetExtension(fullPath);
        if (!extension.Equals(".sln", StringComparison.OrdinalIgnoreCase)
            && !extension.Equals(".slnx", StringComparison.OrdinalIgnoreCase))
            return Result<string>.Fail($"Not a solution file (expected .sln or .slnx): {fullPath}");

        return Result<string>.Ok(fullPath);
    }

    private static bool PathsEqual(string? left, string right)
    {
        if (string.IsNullOrWhiteSpace(left)) return false;
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        return string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), comparison);
    }
}
