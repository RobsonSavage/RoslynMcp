# RoslynMcp

A [Model Context Protocol](https://modelcontextprotocol.io/) server that gives AI coding agents (Claude Code, and any MCP client) **semantic** C# code analysis powered by [Roslyn](https://github.com/dotnet/roslyn) — not text matching.

Instead of grepping `.cs` files, the agent queries the actual compilation: type hierarchies, caller/callee graphs, references that respect overloads and partial classes, data-flow, refactorings with preview/apply, code metrics, and a persistent knowledge/memory/graph layer.

## Why

`grep`/`glob` over source misses overloads, partial classes, extension methods, inheritance, and generated code. RoslynMcp answers questions against the bound semantic model of the loaded solution, so "find references", "who calls this", and "what derives from this" are correct rather than approximate.

## Components

| Project | TFM | Role |
|---------|-----|------|
| `RoslynMcp.Server` | net10.0 | The MCP **stdio** server (executable). Hosts all tools. |
| `RoslynMcp.Core` | netstandard2.0 | Workspace loading, analysis services, refactoring engine, SQLite-backed KB/memory/graph. |
| `RoslynMcp.Shared` | netstandard2.0 | Contracts/DTOs shared between server and extension. |
| `RoslynMcp.Extension` | net472 | Legacy Visual Studio extension (EmbedIO/VS SDK). Superseded by the MCP server — see [docs/MIGRATION.md](docs/MIGRATION.md). |

Built on `Microsoft.CodeAnalysis.CSharp.Workspaces`, `Microsoft.CodeAnalysis.Workspaces.MSBuild`, `Microsoft.Build.Locator`, `ModelContextProtocol`, and Serilog.

## Prerequisites

- .NET 10 SDK (`global.json` pins `10.0.100`, `rollForward: latestMinor`)
- MSBuild (Visual Studio or VS Build Tools) — required for `MSBuildWorkspace` to load solutions
- A target solution that **builds** (analysis fidelity depends on a clean compilation)

## Build & Install

Publish the server to a user-local location (`%LOCALAPPDATA%\RoslynMcp`):

```powershell
.\publish-local.ps1
```

Output: `%LOCALAPPDATA%\RoslynMcp\RoslynMcp.Server.exe` (framework-dependent, plus dependencies).

After a successful publish, `configure-clients.ps1` configures the user-scoped Claude Code
and Codex MCP entries and removes the retired bootstrap environment variable from earlier
installations. Restart Claude Code and Codex after publishing.

To run tests:

```powershell
dotnet test RoslynMcp.sln
```

## Client configuration

`publish-local.ps1` writes these settings. The Claude Code entry in `~/.claude.json` is:

```json
{
  "mcpServers": {
    "roslyn": {
      "type": "stdio",
      "command": "cmd",
      "args": ["/c", "%LOCALAPPDATA%\\RoslynMcp\\RoslynMcp.Server.exe"],
      "timeout": 120000
    }
  }
}
```

Codex receives the matching entry in `~/.codex/config.toml`:

```toml
[mcp_servers.roslyn]
command = "cmd"
args = ["/c", "%LOCALAPPDATA%\\RoslynMcp\\RoslynMcp.Server.exe"]
tool_timeout_sec = 60
startup_timeout_sec = 120
```

No directory argument is needed. The server makes one startup selection from the process working
directory, then keeps that selection until an explicit selector call changes it.

### Solution resolution order

1. `--solution-path <path>` CLI argument
2. `ROSLYNMCP_SOLUTION_PATH` environment variable
3. **Discovery from CWD**: walk up to the enclosing git root (`.git` directory or file, so git
   worktrees are supported), then locate the `.sln`/`.slnx` inside that repo.

Auto-discovery considers every solution filename, including `RoslynMcp.sln`, so the server can analyze its own checkout.

If none resolves, the MCP host starts unselected. `get_workspace_status` reports
`isSolutionSelected: false`; solution-scoped tools return `NO_SOLUTION_SELECTED`; and no config or
SQLite database is created. `set_solution_root` and `set_solution_path` remain available to make the
first selection.

`set_solution_path` switches the workspace, configuration and SQLite database as one operation.
Existing tool calls finish first, and migrations run against the target database before it becomes active.

### Explicit, sticky selection

`set_solution_root` repeats git-root and solution discovery for an explicit repository or worktree
directory. `set_solution_path` selects an exact `.sln` or `.slnx`. Either successful call replaces
the current solution, regardless of how it was selected, and the result remains active until another
selector succeeds. A failed selector leaves the current solution unchanged.

Creating or entering a worktree does not change Roslyn automatically. A workflow that moves work to
another checkout, such as `bug-implement`, must call `set_solution_root` with the verified worktree
root and check that the returned `solutionPath` is inside it. Claude Code, Codex, Gemini and OpenCode
expose the same logical selector with client-specific MCP prefixes.

For multi-worktree / multi-session setups and advanced wrapper-script configuration, see [docs/install-roslyn-mcp.md](docs/install-roslyn-mcp.md).

### Environment variables

| Variable | Purpose | Default |
|----------|---------|---------|
| `ROSLYNMCP_SOLUTION_PATH` | Initial solution selection; either selector can replace it later | (auto-discover or start unselected) |
| `ROSLYNMCP_LOG_DIR` | Serilog file sink directory | `%LOCALAPPDATA%\RoslynMcp\logs` |
| `ROSLYNMCP_WARMUP_PARALLELISM` | Projects compiled in parallel during `--warm-up` | `2` |

The SQLite state location is not configurable by environment variable; see Runtime state below.

## Keeping the index current

MSBuildWorkspace takes a snapshot when it opens the solution and never looks at the filesystem
again. The server closes that gap itself, so tools answer from what is on disk now rather than
from what was there at startup.

A file watcher over the solution directory records what changed; the next tool call applies it
before the tool runs:

| Change on disk | What happens |
|----------------|--------------|
| Text edit to a file already in the solution | That one document is refreshed (milliseconds) |
| File added, deleted or renamed | One full solution reload |
| `.csproj`, `.props`, `.targets`, `.sln`, `.slnx` touched | One full solution reload |
| More than 200 files changed at once (a `git pull` or branch switch) | One full solution reload |
| Watcher buffer overflow | One full solution reload |

Reloads are coalesced: a pull that rewrites 500 files costs one reload on the next tool call, not
500. `bin`, `obj`, `.git`, `.vs`, `node_modules` and `.roslyn-mcp-data` are ignored.

The dependency graph is derived from the project list and project references, so it is rebuilt
after every solution load. Nodes are keyed by project file path, which survives a reload and a
process restart. Nodes and edges you add yourself with `graph_add_node` / `graph_add_edge` are
kept when the derived rows are replaced.

The workspace is also unloaded after a period with no tool calls, and reloaded on the next call.
On a large solution that returns most of the process's memory to the OS between bursts of work;
the reload costs roughly the original load time.

### Configuration

Settings live in `.roslyn-mcp-data\config.json` beside the solution, and are read and written with
`config_get`, `config_set` and `config_list`. A missing file or a missing key falls back to the
default below.

| Key | Default | Effect |
|-----|---------|--------|
| `workspace.watch_files` | `true` | Refresh the workspace from disk when source files change |
| `workspace.idle_unload_minutes` | `30` | Unload the workspace after N minutes idle (0 = never) |
| `graph.auto_rebuild` | `true` | Rebuild the dependency graph after every solution load |
| `logging.file_retention_days` | `7` | Prune matching server logs older than N days at startup |
| `logging.level` | `Information` | Serilog minimum level |
| `timeout.default` | `30` | Default per-tool timeout in seconds. `timeout.<tool_name>` overrides one tool |
| `warmup.enabled` | `false` | Compile projects on start rather than on first query |
| `warmup.parallelism` | `0` | Projects compiled in parallel during warmup (0 = ProcessorCount/2) |
| `paging.default_page_size` | `5` | Page size for paged results when the caller does not ask for one |
| `paging.max_page_size` | `200` | Ceiling on a caller-supplied page size |
| `sqlite.busy_timeout_ms` | `1000` | SQLite busy timeout |
| `sqlite.cache_size_kb` | `16000` | SQLite page cache |

`tools.<tool_name>.enabled` keys hold per-tool enable/disable state and are read and written
through `tool_enabled`. `workspace.follow_roots` is retired: `config_set` rejects it, and loading a
`config.json` that still contains it strips the key and rewrites the file.

## Using the tools

### Coordinates are 0-based, in both directions

Every `line`/`column` parameter is 0-based, and the column must land **inside the identifier
token** - the method, type or variable name itself. A column at the start of the line, on a
modifier keyword or on the return type resolves nothing and the tool answers
`No symbol found at position`. That is a miss, not a fault.

Coordinates the server emits - `text_search` hits, `get_file_outline`, the
`definitions[].location` of `find_definition` - are raw `LinePosition` values, 0-based and
unconverted. So they chain verbatim: feed a `text_search` hit straight into `find_definition`,
`find_callers` or `find_references` with no adjustment. Adding 1 first aims a line late and
produces `No symbol found at position` on a symbol that is really there.

Convert to 1-based only when a coordinate is going in front of a human, into a `file.cs:line`
reference, or into an editor. Roslyn's line 112 is your editor's line 113.

Where a tool accepts a name instead (`get_type_info` takes `typeName`, `understand_type` takes a
fully-qualified type name), prefer the name - it avoids the coordinate question entirely.

### Locate with `text_search`, answer with the semantic tools

`text_search` is grep and carries no more meaning than grep. Use it to find a coordinate, then
answer the question with `find_references`, `find_implementations`, `find_callers` or
`get_method_body`. A text hit on a property shows its declaration and its setter and reads like
evidence of use; `find_references` on the same property is what tells you whether anything
actually calls it.

### `get_workspace_status` has two limits

It confirms `solutionPath` and `isSolutionSelected`, both read from the live workspace. But
`isFullyLoaded` is not a measurement - it is `false` on the unselected path and the literal `true`
on the loaded path, so it can never report a partially loaded workspace. And it takes no
arguments, which makes it the one tool a malformed-argument fault cannot reach. A green status
next to a failing tool means the failing call's arguments are wrong, not that the workspace is
broken. Confirm with a query that passes real parameters.

An `An error occurred invoking '<tool>'` from the MCP host is a rejected call, not a dead server;
the validation detail is usually swallowed by the client. Re-read the tool schema and re-issue.

## Tools

97 tools across the following areas (use `ToolSearch` with the `+roslyn` prefix in Claude Code to discover them):

**Structure** — `get_solution_structure`, `get_project_structure`, `get_file_outline`, `get_types_in_file`, `get_dependency_graph`, `get_full_context`, `get_overloads`, `get_constructor_parameters`, `get_xml_documentation`, `get_accessibility`

**Navigation / search** — `find_references`, `find_definition`, `find_callers`, `find_callees`, `find_implementations`, `find_overrides`, `find_derived_types`, `find_base_members`, `find_extension_methods`, `find_event_subscribers`, `find_attribute_usages`, `find_entry_points`, `find_tests_for_type`, `text_search`

**Analysis** — `understand_type`, `understand_method`, `get_type_info`, `get_type_members`, `get_class_hierarchy`, `get_method_body`, `get_code_metrics`, `analyze_data_flow`, `analyze_operations`, `impact_analysis`, `find_unused_code`, `find_async_issues`, `find_performance_issues`

**Diagnostics** — `get_errors`, `get_warnings`, `get_quick_fixes`, `get_workspace_status`, `reload_file`

**Refactoring** (preview then apply) — `preview_rename`/`apply_rename`, `preview_extract_method`/`apply_extract_method`, `preview_extract_interface`/`apply_extract_interface`, `preview_move_type`/`apply_move_type`, `preview_split_class`/`apply_split_class`, `suggest_refactorings`, `organize_usings`

**Apollo** (compile-error workflow) — `apollo_diagnose`, `apollo_fix`, `apollo_isolate`, `apollo_validate`

**Knowledge base** — `kb_add`, `kb_get`, `kb_update`, `kb_delete`, `kb_list`, `kb_search`, `kb_related`, `kb_stats`

**Memory** — `memory_store`, `memory_retrieve`, `memory_update`, `memory_delete`, `memory_list`, `memory_search`, `memory_consolidate`, `memory_cleanup`, `memory_stats`, `memory_export`, `memory_import`

**Dependency graph** — `graph_add_node`, `graph_add_edge`, `graph_remove_node`, `graph_query_neighbors`, `graph_query_path`, `graph_query_subgraph`, `graph_impact`, `graph_rebuild`, `graph_stats`, `graph_visualize`

**Session / config** — `session_start`, `session_end`, `session_list`, `set_solution_path`, `set_solution_root`, `config_get`, `config_set`, `config_list`, `tool_enabled`, `validate_text`

## Runtime state

Persistent KB/memory/graph data and `config.json` are stored in `.roslyn-mcp-data\` beside the
resolved solution file. If that directory cannot be created (a read-only solution tree), the
server falls back to `%TEMP%\RoslynMcp\<hash of solution dir>\` and logs a warning. Each worktree
resolves its own solution, so it gets its own state and concurrent sessions don't collide. The
whole directory is git-ignored.

## Updating a workstation

The clients run `%LOCALAPPDATA%\RoslynMcp\RoslynMcp.Server.exe`, not your clone, so pulling
source changes has no effect until you re-publish. Run every command from the root of the clone -
`publish-local.ps1` resolves the project by relative path.

```powershell
# 1. Get the change
git pull

# 2. Close Claude Code and Codex, then confirm nothing still holds the exe
Get-Process RoslynMcp.Server -ErrorAction SilentlyContinue | Stop-Process -Force

# 3. Keep a rollback copy
$d = Join-Path $env:LOCALAPPDATA 'RoslynMcp'
if (Test-Path $d) { Copy-Item $d "$d.bak" -Recurse -Force }

# 4. Clean the install directory, preserving log history
if (Test-Path $d) {
    Get-ChildItem $d -Force |
        Where-Object { $_.Name -notin @('logs','extension.log') } |
        Remove-Item -Recurse -Force
}

# 5. Re-publish (this also re-runs configure-clients.ps1)
.\publish-local.ps1

# 6. Restart Claude Code and Codex
```

Steps 2 and 4 are the ones people skip and regret:

- **Step 2**: with a client still running, the exe is locked, `dotnet publish` fails partway, and
  you are left with new DLLs beside an old exe - which fails later, in confusing ways.
- **Step 4**: `dotnet publish -o` overwrites same-named files but never deletes orphans, so the
  install directory accumulates stale assemblies across upgrades.

If step 5 fails, roll back with
`Remove-Item $d -Recurse -Force; Rename-Item "$d.bak" $d`. Delete the `.bak` once the new build
looks right.

### What the re-publish changes

`publish-local.ps1` calls `configure-clients.ps1`, which rewrites the user-scoped MCP entries in
`~/.claude.json` and `~/.codex/config.toml` in place. It is idempotent: an entry that is already
current is left alone, obsolete bootstrap forwarding is removed, and unrelated MCP environment
entries are preserved. Nothing else in those files is touched.

Both clients read MCP configuration only at startup, so the restart in step 6 is what actually
picks up the new server. Per-solution state in `.roslyn-mcp-data\` is untouched by an update.

### Verify the update landed

The server has no `--version` flag. Read the versions out of the install directory instead:

```powershell
$d = Join-Path $env:LOCALAPPDATA 'RoslynMcp'
Get-ChildItem $d -Filter 'ModelContextProtocol*.dll' |
    Select-Object Name, @{n='Version';e={$_.VersionInfo.ProductVersion}}
```

Both entries must read `2.0.0`. A `0.8.0-preview.1` means the install is stale no matter what
`git log` says, because the running server loads from this directory and not from your clone.
Then, inside a restarted client, call `get_workspace_status` and check it reports the solution you
expect.

### If the SDK moved on

`Microsoft.Extensions.Hosting 10.0.10` sets the floor: an older SDK fails at restore. `global.json`
pins `10.0.100` with `rollForward: latestMinor`, so a workstation on .NET 10.0.1xx is fine and one
still on .NET 9 is not.

## Docs

- [docs/install-roslyn-mcp.md](docs/install-roslyn-mcp.md) — full install guide, worktrees, multi-session, troubleshooting
- [docs/MIGRATION.md](docs/MIGRATION.md) — migrating from the v1 Visual Studio extension
