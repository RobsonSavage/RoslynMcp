using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime;
using System.Runtime.CompilerServices;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.CodeAnalysis.Text;
using RoslynMcp.Shared;
using Serilog;

namespace RoslynMcp.Server.Providers;

/// <summary>
/// IWorkspaceProvider backed by MSBuildWorkspace for standalone server mode.
/// Use CreateAsync factory for async initialization.
/// </summary>
public sealed class MsBuildWorkspaceProvider : IWorkspaceProvider, IAsyncDisposable
{
    private MSBuildWorkspace _workspace;
    private string _solutionDir;
    private string _solutionPath;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _reloadLock = new(1, 1);
    private readonly SemaphoreSlim _ensureLock = new(1, 1);
    private volatile ConcurrentDictionary<string, DocumentId> _documentCache = new(StringComparer.OrdinalIgnoreCase);
    private volatile bool _initialized;

    // Idle-unload bookkeeping. _inFlight is incremented for the duration of every
    // tool call so the idle sweep can never unload from under a running request.
    private long _lastActivityTicks = DateTime.UtcNow.Ticks;
    private int _inFlight;
    private CancellationTokenSource? _idleCts;

    public bool HasSolution => _initialized && _workspace.CurrentSolution.ProjectIds.Count > 0;
    public Solution? CurrentSolution => _initialized ? _workspace.CurrentSolution : null;

    /// <summary>True when the solution is loaded; false while idle-unloaded.</summary>
    public bool IsLoaded => _initialized;

    /// <summary>Path of the solution this provider loads, retained across an idle unload.</summary>
    public string SolutionPath => _solutionPath;

    public event EventHandler<SolutionChangedEventArgs>? SolutionChanged;

    private MsBuildWorkspaceProvider(MSBuildWorkspace workspace, string solutionDir, string solutionPath, ILogger logger)
    {
        _workspace = workspace;
        _solutionDir = solutionDir;
        _solutionPath = solutionPath;
        _logger = logger;
        _workspace.WorkspaceChanged += OnWorkspaceChanged;
    }

    /// <summary>
    /// Creates and initializes a workspace provider. MSBuildLocator.RegisterDefaults()
    /// must have been called before invoking this method.
    /// </summary>
    public static async Task<MsBuildWorkspaceProvider> CreateAsync(
        string solutionPath,
        ILogger logger,
        bool warmUp = false,
        CancellationToken ct = default)
    {
        var fullPath = Path.GetFullPath(solutionPath);
        if (!File.Exists(fullPath))
            throw new FileNotFoundException($"Solution not found: {fullPath}");

        var solutionDir = Path.GetDirectoryName(fullPath)!;
        var workspace = MSBuildWorkspace.Create();
        var provider = new MsBuildWorkspaceProvider(workspace, solutionDir, fullPath, logger);

        if (fullPath.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase))
        {
            var projectPaths = ParseSlnx(fullPath, solutionDir);
            logger.Information("Loading {Count} projects from .slnx: {SolutionPath}", projectPaths.Count, fullPath);
            foreach (var projectPath in projectPaths)
            {
                var alreadyLoaded = workspace.CurrentSolution.Projects
                    .Any(p => string.Equals(p.FilePath, projectPath, StringComparison.OrdinalIgnoreCase));
                if (alreadyLoaded)
                {
                    continue;
                }
                await workspace.OpenProjectAsync(projectPath, cancellationToken: ct);
            }
        }
        else
        {
            logger.Information("Loading solution: {SolutionPath}", fullPath);
            await workspace.OpenSolutionAsync(fullPath, cancellationToken: ct);
        }

        provider._initialized = true;
        provider.RebuildDocumentCache();

        foreach (var diag in workspace.Diagnostics)
        {
            if (diag.Kind == WorkspaceDiagnosticKind.Failure)
                logger.Warning("Workspace load issue: {Message}", diag.Message);
        }

        var solution = workspace.CurrentSolution;
        logger.Information("Solution loaded: {ProjectCount} projects, {DocumentCount} documents",
            solution.ProjectIds.Count,
            solution.Projects.Sum(p => p.DocumentIds.Count));

        if (warmUp)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await provider.WarmUpAsync(ct);
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    logger.Warning(ex, "Workspace warm-up failed; tools will load on-demand");
                }
            }, ct);
        }

        return provider;
    }

    public Task<Document?> GetDocumentAsync(string filePath, ProjectId? projectId = null, CancellationToken ct = default)
    {
        if (!_initialized) return Task.FromResult<Document?>(null);

        var normalized = NormalizePath(filePath);

        if (_documentCache.TryGetValue(normalized, out var docId))
        {
            var doc = _workspace.CurrentSolution.GetDocument(docId);
            if (doc != null)
            {
                // If projectId filter specified, verify match
                if (projectId == null || doc.Project.Id == projectId)
                    return Task.FromResult<Document?>(doc);
            }
        }

        // Cache miss: fall back to linear scan (cache may be stale)
        var solution = _workspace.CurrentSolution;
        if (projectId != null)
        {
            var project = solution.GetProject(projectId);
            if (project != null)
            {
                var found = FindDocumentByPath(project.Documents, normalized);
                if (found != null) return Task.FromResult<Document?>(found);
            }
        }

        foreach (var project in solution.Projects)
        {
            var found = FindDocumentByPath(project.Documents, normalized);
            if (found != null) return Task.FromResult<Document?>(found);
        }

        return Task.FromResult<Document?>(null);
    }

    public Task<IReadOnlyList<Document>> GetDocumentsAsync(string filePath, CancellationToken ct = default)
    {
        if (!_initialized) return Task.FromResult<IReadOnlyList<Document>>(Array.Empty<Document>());

        var normalized = NormalizePath(filePath);

        // Full scan needed to find all linked copies across projects
        var results = new List<Document>();
        foreach (var project in _workspace.CurrentSolution.Projects)
        {
            var doc = FindDocumentByPath(project.Documents, normalized);
            if (doc != null) results.Add(doc);
        }

        return Task.FromResult<IReadOnlyList<Document>>(results);
    }

    public Task<Project?> GetProjectAsync(string projectName, CancellationToken ct = default)
    {
        if (!_initialized) return Task.FromResult<Project?>(null);

        var project = _workspace.CurrentSolution.Projects
            .FirstOrDefault(p => string.Equals(p.Name, projectName, StringComparison.OrdinalIgnoreCase));

        return Task.FromResult(project);
    }

    public async Task<bool> TryReloadDocumentAsync(string filePath, CancellationToken ct = default)
    {
        if (!_initialized) return false;

        var normalized = NormalizePath(filePath);
        Document? target = null;

        foreach (var project in _workspace.CurrentSolution.Projects)
        {
            target = FindDocumentByPath(project.Documents, normalized);
            if (target != null) break;
        }

        if (target == null)
        {
            return false;
        }

        if (!File.Exists(normalized))
        {
            _logger.Warning("TryReloadDocument: file not on disk: {FilePath}", normalized);
            return false;
        }

        try
        {
            using var stream = new FileStream(normalized, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var sourceText = SourceText.From(stream);
            var newSolution = _workspace.CurrentSolution.WithDocumentText(target.Id, sourceText);
            var applied = _workspace.TryApplyChanges(newSolution);

            if (!applied)
                _logger.Warning("TryApplyChanges failed for: {FilePath}", filePath);

            return applied;
        }
        catch (IOException ex)
        {
            _logger.Error(ex, "Failed to reload document: {FilePath}", filePath);
            return false;
        }
    }

    public async Task<bool> ReloadSolutionAsync(string solutionPath, bool warmUp = false, CancellationToken ct = default)
    {
        var fullPath = Path.GetFullPath(solutionPath);
        if (!File.Exists(fullPath))
            throw new FileNotFoundException($"Solution not found: {fullPath}");

        await _reloadLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var newSolutionDir = Path.GetDirectoryName(fullPath)!;
            var newWorkspace = MSBuildWorkspace.Create();

            _logger.Information("Switching solution to: {SolutionPath}", fullPath);

            if (fullPath.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase))
            {
                var projectPaths = ParseSlnx(fullPath, newSolutionDir);
                _logger.Information("Loading {Count} projects from .slnx", projectPaths.Count);
                foreach (var projectPath in projectPaths)
                {
                    var alreadyLoaded = newWorkspace.CurrentSolution.Projects
                        .Any(p => string.Equals(p.FilePath, projectPath, StringComparison.OrdinalIgnoreCase));
                    if (alreadyLoaded) continue;
                    await newWorkspace.OpenProjectAsync(projectPath, cancellationToken: ct).ConfigureAwait(false);
                }
            }
            else
            {
                await newWorkspace.OpenSolutionAsync(fullPath, cancellationToken: ct).ConfigureAwait(false);
            }

            // Swap
            var oldWorkspace = _workspace;
            oldWorkspace.WorkspaceChanged -= OnWorkspaceChanged;

            _workspace = newWorkspace;
            _solutionDir = newSolutionDir;
            _solutionPath = fullPath;
            _workspace.WorkspaceChanged += OnWorkspaceChanged;
            _initialized = true;
            RebuildDocumentCache();

            var solution = _workspace.CurrentSolution;
            _logger.Information("Solution switched: {ProjectCount} projects, {DocumentCount} documents",
                solution.ProjectIds.Count,
                solution.Projects.Sum(p => p.DocumentIds.Count));

            // Dispose old workspace on background thread (in-flight operations hold immutable Solution snapshots)
            _ = Task.Run(() =>
            {
                try { oldWorkspace.Dispose(); }
                catch (Exception ex) { _logger.Warning(ex, "Failed to dispose previous workspace"); }
            }, CancellationToken.None);

            SolutionChanged?.Invoke(this, new SolutionChangedEventArgs(null, solution));

            if (warmUp)
            {
                _ = Task.Run(async () =>
                {
                    try { await WarmUpAsync(ct); }
                    catch (OperationCanceledException) { }
                    catch (Exception ex) { _logger.Warning(ex, "Warm-up failed after solution switch"); }
                }, ct);
            }

            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.Error(ex, "Failed to switch solution to: {SolutionPath}", fullPath);
            throw;
        }
        finally
        {
            _reloadLock.Release();
        }
    }

    // ── Idle unload ──

    /// <summary>
    /// Marks the start of a request. Call in a using/try around every tool invocation so the
    /// idle sweep cannot unload the workspace while a call is still using it.
    /// </summary>
    public void EnterRequest()
    {
        Interlocked.Increment(ref _inFlight);
        Volatile.Write(ref _lastActivityTicks, DateTime.UtcNow.Ticks);
    }

    /// <summary>Marks the end of a request and restarts the idle clock.</summary>
    public void ExitRequest()
    {
        Interlocked.Decrement(ref _inFlight);
        Volatile.Write(ref _lastActivityTicks, DateTime.UtcNow.Ticks);
    }

    /// <summary>
    /// Reloads the solution if it was idle-unloaded. No-op when already loaded, so the cost on
    /// the hot path is a single volatile read.
    /// </summary>
    public async Task EnsureLoadedAsync(CancellationToken ct = default)
    {
        if (_initialized) return;

        await _ensureLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_initialized) return;

            _logger.Information("Reloading workspace after idle unload: {SolutionPath}", _solutionPath);
            var sw = Stopwatch.StartNew();
            await ReloadSolutionAsync(_solutionPath, warmUp: false, ct).ConfigureAwait(false);
            _logger.Information("Workspace reloaded in {ElapsedMs} ms", sw.ElapsedMilliseconds);
        }
        finally
        {
            _ensureLock.Release();
        }
    }

    /// <summary>
    /// Drops the loaded solution and returns its heap to the OS. Returns false if the workspace
    /// is already unloaded or a request is in flight.
    /// </summary>
    public async Task<bool> UnloadAsync(CancellationToken ct = default)
    {
        await _ensureLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!_initialized) return false;
            if (Volatile.Read(ref _inFlight) > 0) return false;

            MSBuildWorkspace oldWorkspace;
            Solution? oldSolution;

            await _reloadLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                oldWorkspace = _workspace;
                oldSolution = oldWorkspace.CurrentSolution;
                oldWorkspace.WorkspaceChanged -= OnWorkspaceChanged;

                // Swap in an empty workspace rather than nulling the field: every public member
                // already guards on _initialized, so this keeps the existing null semantics.
                _workspace = MSBuildWorkspace.Create();
                _documentCache = new ConcurrentDictionary<string, DocumentId>(StringComparer.OrdinalIgnoreCase);
                _initialized = false;
            }
            finally
            {
                _reloadLock.Release();
            }

            var before = GC.GetTotalMemory(false);

            try { oldWorkspace.Dispose(); }
            catch (Exception ex) { _logger.Warning(ex, "Failed to dispose workspace during idle unload"); }

            // Clear every remaining reference to the old solution graph before collecting.
            // Without this the locals still root it: under a debug JIT (and inside an async
            // state machine, where locals are fields) they stay live for the whole method.
            if (oldSolution != null)
                SolutionChanged?.Invoke(this, new SolutionChangedEventArgs(oldSolution, null));
            oldWorkspace = null!;
            oldSolution = null;

            var after = CompactHeap();

            _logger.Information(
                "Workspace idle-unloaded: {SolutionPath}. Managed heap {BeforeMb} MB -> {AfterMb} MB",
                _solutionPath, before / (1024 * 1024), after / (1024 * 1024));

            return true;
        }
        finally
        {
            _ensureLock.Release();
        }
    }

    /// <summary>
    /// Dropping references is not enough: without a compacting collect the freed heap stays as
    /// reserved commit, which is the entire point of unloading. Kept separate and non-inlined so
    /// no caller local can still root the released graph while the collection runs.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static long CompactHeap()
    {
        GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        return GC.GetTotalMemory(false);
    }

    /// <summary>
    /// Starts the background sweep that unloads the workspace after <paramref name="idleTimeout"/>
    /// without a tool call. A non-positive timeout disables idle unloading.
    /// </summary>
    public void StartIdleMonitor(TimeSpan idleTimeout, CancellationToken ct = default)
    {
        if (idleTimeout <= TimeSpan.Zero)
        {
            _logger.Information("Idle workspace unload disabled");
            return;
        }

        _idleCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var token = _idleCts.Token;

        // Check often enough to unload promptly after the threshold, but never more than
        // once a minute for a long timeout.
        var interval = TimeSpan.FromSeconds(Math.Clamp(idleTimeout.TotalSeconds / 4, 30, 60));

        _ = Task.Run(async () =>
        {
            using var timer = new PeriodicTimer(interval);
            while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
            {
                try
                {
                    if (!_initialized) continue;

                    var last = new DateTime(Volatile.Read(ref _lastActivityTicks), DateTimeKind.Utc);
                    if (DateTime.UtcNow - last >= idleTimeout)
                        await UnloadAsync(token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex) { _logger.Warning(ex, "Idle unload check failed"); }
            }
        }, token);

        _logger.Information(
            "Idle workspace unload enabled: unload after {Minutes} min idle, checked every {Interval}s",
            idleTimeout.TotalMinutes, interval.TotalSeconds);
    }

    public ValueTask DisposeAsync()
    {
        _idleCts?.Cancel();
        _idleCts?.Dispose();
        _workspace.WorkspaceChanged -= OnWorkspaceChanged;
        _workspace.Dispose();
        _reloadLock.Dispose();
        _ensureLock.Dispose();
        return default;
    }

    /// <summary>Solution directory for path resolution and security checks.</summary>
    public string SolutionDirectory => _solutionDir;

    private void OnWorkspaceChanged(object? sender, WorkspaceChangeEventArgs e)
    {
        if (e.Kind == WorkspaceChangeKind.SolutionChanged
            || e.Kind == WorkspaceChangeKind.SolutionReloaded
            || e.Kind == WorkspaceChangeKind.DocumentAdded
            || e.Kind == WorkspaceChangeKind.DocumentRemoved)
        {
            RebuildDocumentCache();
        }

        // When a document changes, its FilePath may have been renamed,
        // making the path-based cache stale. Update the cache entry.
        if (e.Kind == WorkspaceChangeKind.DocumentChanged && e.DocumentId != null)
        {
            var newDoc = e.NewSolution.GetDocument(e.DocumentId);
            var oldDoc = e.OldSolution.GetDocument(e.DocumentId);
            if (oldDoc?.FilePath != null && newDoc?.FilePath != null
                && !PathsEqual(oldDoc.FilePath, newDoc.FilePath))
            {
                // FilePath changed (rename): remove old entry and add new one
                var cache = _documentCache;
                cache.TryRemove(NormalizePath(oldDoc.FilePath), out _);
                cache.TryAdd(NormalizePath(newDoc.FilePath), e.DocumentId);
            }
        }

        if (e.Kind == WorkspaceChangeKind.SolutionChanged
            || e.Kind == WorkspaceChangeKind.SolutionReloaded)
        {
            SolutionChanged?.Invoke(this, new SolutionChangedEventArgs(e.OldSolution, e.NewSolution));
        }
    }

    private void RebuildDocumentCache()
    {
        // Build into a new dictionary, then swap atomically to avoid
        // concurrent GetDocumentAsync seeing a partially-cleared cache.
        var newCache = new ConcurrentDictionary<string, DocumentId>(StringComparer.OrdinalIgnoreCase);
        foreach (var project in _workspace.CurrentSolution.Projects)
        {
            foreach (var doc in project.Documents)
            {
                if (doc.FilePath != null)
                    newCache.TryAdd(NormalizePath(doc.FilePath), doc.Id);
            }
        }

        _documentCache = newCache;
    }

    private static Document? FindDocumentByPath(IEnumerable<Document> documents, string normalizedPath)
    {
        foreach (var doc in documents)
        {
            if (doc.FilePath != null && PathsEqual(doc.FilePath, normalizedPath))
                return doc;
        }
        return null;
    }

    private static string NormalizePath(string path)
    {
        return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static bool PathsEqual(string a, string b)
    {
        return string.Equals(
            NormalizePath(a),
            NormalizePath(b),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }

    /// <summary>
    /// Parses a .slnx (XML solution) file and returns absolute paths to all referenced projects.
    /// </summary>
    private static List<string> ParseSlnx(string slnxPath, string solutionDir)
    {
        var doc = XDocument.Load(slnxPath);
        return doc.Descendants("Project")
            .Select(el => el.Attribute("Path")?.Value)
            .Where(p => !string.IsNullOrEmpty(p))
            .Select(p => Path.GetFullPath(Path.Combine(solutionDir, p!)))
            .ToList();
    }

    private async Task WarmUpAsync(CancellationToken ct)
    {
        _logger.Information("Starting background warm-up");
        var projects = _workspace.CurrentSolution.Projects
            .OrderByDescending(p => p.ProjectReferences.Count())
            .ToList();

        var parallelism = int.TryParse(
            Environment.GetEnvironmentVariable("ROSLYNMCP_WARMUP_PARALLELISM"),
            out var p) && p > 0 ? p : 2;
        var loaded = 0;

        await Parallel.ForEachAsync(projects, new ParallelOptions
        {
            MaxDegreeOfParallelism = parallelism,
            CancellationToken = ct
        }, async (project, token) =>
        {
            try
            {
                await project.GetCompilationAsync(token);
                var count = Interlocked.Increment(ref loaded);
                if (count % 10 == 0)
                    _logger.Information("Warm-up progress: {Loaded}/{Total}", count, projects.Count);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                _logger.Warning(ex, "Warm-up failed for {Project}", project.Name);
            }
        });

        _logger.Information("Warm-up complete: {Loaded}/{Total} projects compiled", loaded, projects.Count);
    }
}
