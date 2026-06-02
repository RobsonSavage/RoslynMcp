# RoslynMcp

A [Model Context Protocol](https://modelcontextprotocol.io/) server that gives AI coding agents (Claude Code, and any MCP client) **semantic** C# code analysis powered by [Roslyn](https://github.com/dotnet/roslyn) — not text matching.

Instead of grepping `.cs` files, the agent queries the actual compilation: type hierarchies, caller/callee graphs, references that respect overloads and partial classes, data-flow, refactorings with preview/apply, code metrics, and a persistent knowledge/memory/graph layer.

## Why

`grep`/`glob` over source misses overloads, partial classes, extension methods, inheritance, and generated code. RoslynMcp answers questions against the bound semantic model of the loaded solution, so "find references", "who calls this", and "what derives from this" are correct rather than approximate.

## Components

| Project | TFM | Role |
|---------|-----|------|
| `RoslynMcp.Server` | net10.0 | The MCP **stdio** server (executable). Hosts all tools. |
| `RoslynMcp.Core` | — | Workspace loading, analysis services, refactoring engine, SQLite-backed KB/memory/graph. |
| `RoslynMcp.Shared` | — | Contracts/DTOs shared between server and extension. |
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

If the working directory is not inside a git repository, the server refuses to guess (it will not scan unrelated sibling trees) and exits with a clear message. Start Claude from inside the repository you want analyzed, or pin the path via `--solution-path` / `ROSLYNMCP_SOLUTION_PATH`.

For multi-worktree / multi-session setups and advanced wrapper-script configuration, see [docs/install-roslyn-mcp.md](docs/install-roslyn-mcp.md).

### Environment variables

| Variable | Purpose | Default |
|----------|---------|---------|
| `ROSLYNMCP_SOLUTION_PATH` | Explicit solution to load | (auto-discover) |
| `ROSLYNMCP_LOG_DIR` | Serilog file sink directory | `%LOCALAPPDATA%\RoslynMcp\logs` |
| `ROSLYNMCP_DATA_DIR` | SQLite state location (KB/memory/graph) | `.roslyn-mcp-v2/` in the workspace |

## Tools

96 tools across the following areas (use `ToolSearch` with the `+roslyn` prefix in Claude Code to discover them):

**Structure** — `get_solution_structure`, `get_project_structure`, `get_file_outline`, `get_types_in_file`, `get_dependency_graph`, `get_full_context`, `get_overloads`, `get_constructor_parameters`, `get_xml_documentation`, `get_accessibility`

**Navigation / search** — `find_references`, `find_definition`, `find_callers`, `find_callees`, `find_implementations`, `find_overrides`, `find_derived_types`, `find_base_members`, `find_extension_methods`, `find_event_subscribers`, `find_attribute_usages`, `find_entry_points`, `find_tests_for_type`, `text_search`

**Analysis** — `understand_type`, `understand_method`, `get_type_info`, `get_type_members`, `get_class_hierarchy`, `get_method_body`, `get_code_metrics`, `analyze_data_flow`, `analyze_operations`, `impact_analysis`, `find_unused_code`, `find_async_issues`, `find_performance_issues`

**Diagnostics** — `get_errors`, `get_warnings`, `get_quick_fixes`, `get_workspace_status`, `reload_file`

**Refactoring** (preview then apply) — `preview_rename`/`apply_rename`, `preview_extract_method`/`apply_extract_method`, `preview_extract_interface`/`apply_extract_interface`, `preview_move_type`/`apply_move_type`, `preview_split_class`/`apply_split_class`, `suggest_refactorings`, `organize_usings`

**Apollo** (compile-error workflow) — `apollo_diagnose`, `apollo_fix`, `apollo_isolate`, `apollo_validate`

**Knowledge base** — `kb_add`, `kb_get`, `kb_update`, `kb_delete`, `kb_list`, `kb_search`, `kb_related`, `kb_stats`

**Memory** — `memory_store`, `memory_retrieve`, `memory_update`, `memory_delete`, `memory_list`, `memory_search`, `memory_consolidate`, `memory_cleanup`, `memory_stats`, `memory_export`, `memory_import`

**Dependency graph** — `graph_add_node`, `graph_add_edge`, `graph_remove_node`, `graph_query_neighbors`, `graph_query_path`, `graph_query_subgraph`, `graph_impact`, `graph_rebuild`, `graph_stats`, `graph_visualize`

**Session / config** — `session_start`, `session_end`, `session_list`, `set_solution_path`, `config_get`, `config_set`, `config_list`, `tool_enabled`, `validate_text`

## Runtime state

Persistent KB/memory/graph data is stored as SQLite under `.roslyn-mcp-v2/` in the workspace (configurable via `ROSLYNMCP_DATA_DIR`). This directory and `*.db` files are git-ignored. Each worktree gets its own state, so concurrent sessions don't collide.

## Updating

After pulling server changes, re-publish and restart Claude Code:

```powershell
.\publish-local.ps1
```

## Docs

- [docs/install-roslyn-mcp.md](docs/install-roslyn-mcp.md) — full install guide, worktrees, multi-session, troubleshooting
- [docs/MIGRATION.md](docs/MIGRATION.md) — migrating from the v1 Visual Studio extension
