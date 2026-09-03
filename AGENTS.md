# AGENTS.md

Orientation for an AI agent working in this repository. Read this before changing code; it covers
the layering rules, the places a change has to touch, and the traps that have already bitten.

## What this is

An MCP stdio server that answers semantic C# questions against a live Roslyn compilation, plus a
SQLite-backed knowledge base, memory store and dependency graph. 96 tools. The consumer is another
AI agent (Claude Code), not a human clicking buttons, so tool output shape and error messages
matter as much as correctness.

## Layout

| Path | TFM | Contains |
|------|-----|----------|
| `src/RoslynMcp.Shared` | netstandard2.0 | Contracts (request/response records), `Result<T>`, `IWorkspaceProvider`, `ValidationLimits`, `PathValidator` |
| `src/RoslynMcp.Core` | netstandard2.0 | All analysis logic: `Services/*`, `Helpers/*`, SQLite pool and migrations |
| `src/RoslynMcp.Server` | net10.0 | `Program.cs` (host, DI, filters), `Tools/*` (MCP attributes), `Providers/MsBuildWorkspaceProvider` |
| `src/RoslynMcp.Extension` | net472 | Legacy Visual Studio extension. Superseded; do not add features here |
| `tests/RoslynMcp.Core.Tests` | net10.0 | Unit tests over an `AdhocWorkspace`, no MSBuild |
| `tests/RoslynMcp.Integration` | net10.0 | Tests that load this repo's own solution through MSBuild |

Dependencies run one way: Shared <- Core <- Server. Core never references Server. A service must
not know it is being called over MCP.

**Core and Shared target netstandard2.0.** `LangVersion` is `latest`, so modern syntax compiles,
but the BCL surface is old. `Polyfills.cs` supplies `IsExternalInit` and friends. If a `System.*`
API you reach for does not exist there, it is a netstandard2.0 gap, not a mistake in your code.
Only `RoslynMcp.Server` and the test projects get the .NET 10 BCL.

Packages are centrally versioned in `Directory.Packages.props`. A `PackageReference` in a csproj
carries no `Version` attribute; add the version to the central file.

## Adding a tool

Five files, in this order:

1. `src/RoslynMcp.Shared/Contracts/<Area>/<Area>Contracts.cs` - a request record and a response
   record. Positional records with defaults, no logic.
2. `src/RoslynMcp.Core/Services/<Area>Service*.cs` - the implementation, returning
   `Result<TResponse>`. Services are `partial` and split by concern (`SearchService.TextSearch.cs`,
   `GraphService.Queries.cs`), so add to the file that matches, or create a new partial.
3. `src/RoslynMcp.Server/Tools/<Area>Tools.cs` - the `[McpServerTool(Name = "...")]` wrapper.
4. `src/RoslynMcp.Server/Program.cs` - only if a new service or singleton is needed.
5. `tests/RoslynMcp.Core.Tests/Services/<Area>ServiceTests*.cs` - tests go against the service, not
   the tool wrapper.

Every tool wrapper follows one shape. Copy it exactly; the metrics and error handling are not
optional:

```csharp
// ── 1. graph_add_node ──

[McpServerTool(Name = "graph_add_node"), Description("Add a node to the dependency graph")]
public async Task<CallToolResult> GraphAddNode(
    [Description("Unique identifier for the node")] string id,
    CancellationToken ct = default)
{
    var sw = Stopwatch.StartNew();
    bool isError = false;
    try
    {
        if (string.IsNullOrWhiteSpace(id)) return _mapper.Error("id is required");
        var result = await _service.AddNodeAsync(new GraphAddNodeRequest(id), ct);
        if (!result.IsSuccess)
        {
            isError = true;
            return _mapper.Error(result.Error?.Message ?? "Unknown error");
        }
        return _mapper.Success(result.Value);
    }
    catch (OperationCanceledException) { throw; }
    catch (Exception ex)
    {
        isError = true;
        return _mapper.Exception(ex, _logger);
    }
    finally
    {
        _metrics.Record("graph_add_node", sw.Elapsed, isError);
    }
}
```

Notes on that shape:

- `OperationCanceledException` is rethrown, never converted to an error result.
- Argument validation happens in the wrapper (`ToolValidation.ValidateFilePath` for paths), so the
  service can assume well-formed input but still returns `Result` failures for domain errors.
- Every parameter needs a `[Description]`; it is the only thing the calling agent sees.
- Tools are discovered by `WithToolsFromAssembly()`. There is no registration list to update.
- Tool classes are constructed per call, so they hold no state.

`Result<T>` throws from `Value` in DEBUG builds when `IsSuccess` is false. Check `IsSuccess` first.

## Workspace lifecycle

`MsBuildWorkspaceProvider` is a singleton holding one `MSBuildWorkspace`. Three behaviours are
layered on it, and all three are coordinated through the CallTool filter in `Program.cs`:

- **Idle unload.** After `workspace.idle_unload_minutes` with no tool call, the solution is dropped
  and the heap compacted. The next call reloads it, which costs whatever the original load cost.
- **Disk staleness.** A `FileSystemWatcher` records changed paths; the next call refreshes those
  documents, or reloads the whole solution for structural changes. See "Keeping the index current"
  in the README.
- **In-flight counting.** `_inFlight` stops a sweep unloading under a running call.

Rules if you touch this:

- Request paths call `BeginRequestAsync(needsWorkspace, ct)` and `ExitRequest()` in a finally.
  Never call the bare `EnterRequest()` on a path where the idle sweep can run: incrementing the
  counter is not atomic with the sweep's check of it, and the request will read a null document
  from a workspace that was unloaded a microsecond earlier.
- `NeedsWorkspace(toolName)` in `Program.cs` decides whether a tool forces a reload. Memory, KB,
  session and config tools do not need a solution; anything else does. Add new prefixes there
  rather than making the tool tolerate an unloaded workspace.
- Do not add work to the watcher event handlers. They record a path and return; all reloading
  happens on the request thread.

## SQLite

`SqliteConnectionPool` hands out reader and writer leases; writers are serialised. `PRAGMA
foreign_keys=ON`, so `GraphEdges` rows constrain `GraphNodes` deletes.

Migrations are append-only. To change the schema:

1. Add `src/RoslynMcp.Core/Helpers/Migrations/V<N>_<Name>.cs` implementing `IMigration` with the
   next `Version`. Never edit an existing migration; it has already run on live databases.
2. Register it in the `migrations` array in `Program.cs`.
3. Register it in the migration array of every affected test fixture. `GraphServiceTests` and
   `KBServiceTests` each build their own list, and a test whose list omits your migration fails on
   a missing column rather than on anything meaningful.

The database lives in `.roslyn-mcp-data\` beside the resolved solution, so every git worktree gets
its own.

## Configuration

Keys are declared with their defaults in `ConfigManager` and stored in
`.roslyn-mcp-data\config.json`. `Get()` returns a null `Value` when the file or the key is absent,
so read a key like this and fall back to the declared default rather than to zero or false:

```csharp
var cfg = configManager.Get("workspace.idle_unload_minutes");
var minutes = int.TryParse(cfg.Value, out var v) ? v
    : int.TryParse(cfg.DefaultValue, out var d) ? d
    : 0;
```

## Tests

```powershell
dotnet build -c Release
dotnet test -c Release --no-build
```

Roughly 300 tests, all currently green. Unit tests build an `AdhocWorkspace` through
`WorkspaceTestHelper` (`CreateProvider()` returns an `IWorkspaceProvider`), which is fast and needs
no MSBuild. Integration tests load `RoslynMcp.sln` for real, take about 30 seconds, and must
register MSBuild once under a static lock (`MSBuildLocator.IsRegistered`).

Integration tests that load or unload a workspace belong in `[Collection("workspace")]`. They churn
the heap and race each other otherwise, and the heap-growth assertion in `IdleUnloadTest` is
meaningless if another class is loading a solution alongside it.

## Traps

- **`MSBuildWorkspace.TryApplyChanges` writes the document to disk.** It is the only public route
  to update `CurrentSolution`, so refreshing a document from disk writes it straight back. With a
  file watcher running that is a feedback loop, which is why `ReloadDocumentCoreAsync` compares
  text and skips the apply when it matches.
- **`ProjectId` is a fresh GUID on every solution load.** Anything persisted must key off the
  project file path instead. The dependency graph used to key nodes by `ProjectId` and was orphaned
  by every restart.
- **`ROSLYNMCP_SOLUTION_PATH` beats CWD discovery.** A stale User-scope environment variable makes
  every worktree analyse the same solution, and it is invisible in the logs unless you look for the
  resolved path. Check it first when a worktree reports the wrong code.
- **Environment variables are captured at process spawn.** Changing one does not reach a running
  server; the Claude session has to restart.
- **Log retention does not prune.** `retainedFileCountLimit: 7` does not span the `_NNN` suffixed
  files that multiple concurrent servers produce.
- **Services registered `AddTransient` get a new instance per tool call.** Any counter or cache on
  a service field silently resets. `GraphService` is a singleton for exactly this reason.

## Conventions

- Sections inside a large file are separated by a numbered box comment matching the tool order:
  `// ── 10. graph_rebuild ──`. Keep the numbering in step with the class.
- Comments explain why, not what. Prefer no comment to a restatement of the code.
- ASCII only in source and docs. No em dash or en dash; use a plain hyphen.
- Serilog levels: ERROR for a failed operation, WARN for degraded or recovered, INFO for a state
  change, DEBUG for diagnostics. Structured properties, not interpolation.
- Never write a key, token or connection string into a file. Reference the environment variable.
- Do not add or upgrade a package without being asked.
