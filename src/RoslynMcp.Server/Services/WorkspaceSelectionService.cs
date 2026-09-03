using RoslynMcp.Core.Helpers;
using RoslynMcp.Core.Services;
using RoslynMcp.Shared;
using RoslynMcp.Shared.Contracts.Util;
using Serilog;

namespace RoslynMcp.Server.Services;

internal sealed class WorkspaceSelectionService : IWorkspaceSelectionService, IDisposable
{
    private readonly IWorkspaceProvider _workspace;
    private readonly ISolutionContextSwitcher _switcher;
    private readonly ConfigManager _config;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _sessionFollowEnabled;
    private string? _disabledReason;

    public WorkspaceSelectionService(
        IWorkspaceProvider workspace,
        ISolutionContextSwitcher switcher,
        ConfigManager config,
        bool hasExplicitStartupPin,
        ILogger logger)
    {
        _workspace = workspace;
        _switcher = switcher;
        _config = config;
        _logger = logger;
        _sessionFollowEnabled = !hasExplicitStartupPin;
        _disabledReason = hasExplicitStartupPin
            ? "Root following is disabled because the server started with an explicit solution path"
            : null;
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
            _sessionFollowEnabled = false;
            _disabledReason = "Root following is disabled after a manual set_solution_path call";
            _logger.Information("Workspace root following disabled after manual solution selection");
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
            if (!_sessionFollowEnabled)
                return CurrentResponse(fullRootPath, followEnabled: false, _disabledReason);

            if (!ReadFollowConfig())
                return CurrentResponse(
                    fullRootPath,
                    followEnabled: false,
                    "Root following is disabled by workspace.follow_roots");

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

            // A root that holds no solution is the ordinary case for a Python repository or
            // a notes tree, not a fault: the server goes on serving whatever it already had,
            // which is what the bootstrap solution exists for. Report that path rather than
            // an error, so a client asking where it is pointed gets an answer. With nothing
            // loaded there is no true path to report, and it stays a failure.
            if (solutionPath == null)
                return _workspace.CurrentSolution is null
                    ? Result<SetSolutionRootResponse>.Fail(
                        $"No .sln or .slnx found in the git repository containing: {fullRootPath}")
                    : CurrentResponse(
                        fullRootPath,
                        followEnabled: true,
                        $"No .sln or .slnx found in the git repository containing: {fullRootPath}. " +
                        "Keeping the solution the workspace already has.");

            var currentPath = _workspace.CurrentSolution?.FilePath;
            if (PathsEqual(currentPath, solutionPath))
                return CurrentResponse(fullRootPath, followEnabled: true, "Workspace already uses the discovered solution");

            try
            {
                var switched = await _switcher.SwitchAsync(solutionPath, request.WarmUp, ct).ConfigureAwait(false);
                var stillEnabled = ReadFollowConfig();
                return Result<SetSolutionRootResponse>.Ok(new SetSolutionRootResponse(
                    fullRootPath,
                    switched.SolutionPath,
                    switched.ProjectCount,
                    switched.DocumentCount,
                    Changed: true,
                    FollowEnabled: stillEnabled,
                    switched.PreviousSolutionPath,
                    stillEnabled ? null : "Root following is disabled by the target solution configuration"));
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

    private Result<SetSolutionRootResponse> CurrentResponse(
        string rootPath,
        bool followEnabled,
        string? message)
    {
        var solution = _workspace.CurrentSolution;
        var solutionPath = solution?.FilePath ?? string.Empty;
        return Result<SetSolutionRootResponse>.Ok(new SetSolutionRootResponse(
            rootPath,
            solutionPath,
            solution?.ProjectIds.Count ?? 0,
            solution?.Projects.Sum(project => project.DocumentIds.Count) ?? 0,
            Changed: false,
            FollowEnabled: followEnabled,
            Message: message));
    }

    private bool ReadFollowConfig()
    {
        var entry = _config.Get("workspace.follow_roots");
        return bool.TryParse(entry.Value, out var configured)
            ? configured
            : !bool.TryParse(entry.DefaultValue, out var defaultValue) || defaultValue;
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
