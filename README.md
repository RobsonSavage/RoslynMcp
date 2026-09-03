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

To run tests:

```powershell
dotnet test RoslynMcp.sln
```

## Configure (Claude Code)

Add to `~/.claude.json` under `mcpServers`:

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

No directory argument is needed. The server resolves the solution from the working directory Claude launches it in — see below.

### Solution resolution order

1. `--solution-path <path>` CLI argument
2. `ROSLYNMCP_SOLUTION_PATH` environment variable
3. **Auto-discovery from CWD**: walk up to the enclosing git root (`.git` directory *or* file — git **worktrees** are supported), then locate the `.sln`/`.slnx` inside that repo.

Auto-discovery considers every solution filename, including `RoslynMcp.sln`, so the server can analyze its own checkout.

`set_solution_path` switches the workspace, configuration and SQLite database as one operation.
Existing tool calls finish first, and migrations run against the target database before it becomes active.

Clients that can report their current directory can call `set_solution_root`. It repeats the same
git-root and solution discovery, no-ops when the solution is unchanged, and uses the complete
context switch when it changes. Explicit startup paths and a successful manual `set_solution_path`
call pin the server for that process. Claude Code and Codex hook plugins are documented in
[docs/install-roslyn-mcp.md](docs/install-roslyn-mcp.md).

### Install workspace-follow plugins

The server must be configured under the MCP name `roslyn`. Do not set `--solution-path` or
`ROSLYNMCP_SOLUTION_PATH` when workspace following is wanted.

On each Claude Code workstation, run these commands inside Claude Code, then restart it:

```text
/plugin marketplace add RobsonSavage/RoslynMcp
/plugin install roslyn-workspace-follow@roslyn-mcp
```

The Claude plugin handles ordinary directory changes and calls `set_solution_root` before each
Roslyn tool. The pre-tool call is required because `EnterWorktree` does not emit `CwdChanged` in
Claude Code 2.1.259.

On each Codex workstation, run:

```text
codex plugin marketplace add RobsonSavage/RoslynMcp
codex plugin add roslyn-workspace-follow@roslyn-mcp
```

Review and trust the installed hook, then start a new thread. Codex has no directory-change event,
so the plugin calls `set_solution_root` before each `mcp__roslyn__*` tool. This flow was verified
with a fresh Codex thread rooted in a different checkout. `get_workspace_status` reported that
checkout's solution and recorded one `set_solution_root` invocation.

OpenCode 1.18.27 reads the server instruction to call `set_solution_root`, but its plugin API cannot
invoke a tool on an existing MCP connection. Start a new OpenCode session inside each worktree when
deterministic selection is required.

If the working directory is not inside a git repository, the server refuses to guess (it will not scan unrelated sibling trees) and exits with a clear message. Start Claude from inside the repository you want analyzed, or pin the path via `--solution-path` / `ROSLYNMCP_SOLUTION_PATH`.

For multi-worktree / multi-session setups and advanced wrapper-script configuration, see [docs/install-roslyn-mcp.md](docs/install-roslyn-mcp.md).

### Environment variables

| Variable | Purpose | Default |
|----------|---------|---------|
| `ROSLYNMCP_SOLUTION_PATH` | Explicit solution to load | (auto-discover) |
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
| `workspace.follow_roots` | `true` | Allow client hooks to follow reported workspace roots |
| `graph.auto_rebuild` | `true` | Rebuild the dependency graph after every solution load |
| `logging.file_retention_days` | `7` | Prune matching server logs older than N days at startup |

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

## Updating

After pulling server changes, re-publish and restart Claude Code:

```powershell
.\publish-local.ps1
```

## Docs

- [docs/install-roslyn-mcp.md](docs/install-roslyn-mcp.md) — full install guide, worktrees, multi-session, troubleshooting
- [docs/MIGRATION.md](docs/MIGRATION.md) — migrating from the v1 Visual Studio extension
