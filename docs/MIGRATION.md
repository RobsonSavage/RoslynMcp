# Migrating from RoslynMCP v1 (Extension) to v2 (Standalone)

This guide covers switching from the proprietary RoslynMCP Visual Studio extension (v1.17.x) to the clean-room standalone server (v2).

## Key Differences

| Aspect | v1 (Extension) | v2 (Standalone) |
|--------|---------------|-----------------|
| Runtime | VS extension + proxy | Standalone .NET 10 process |
| Transport | stdio proxy -> HTTP -> VS | stdio (direct) |
| MCP tools | 3 meta-tools (`search_tools`, `call_tool`, `get_tool_schema`) | 94 direct tools |
| Data directory | `.roslyn-mcp/` | `.roslyn-mcp-v2/` |
| Database | `memory.db` | `roslyn-mcp.db` (new schema) |
| Config | `.roslyn-mcp/config.json` | `.roslyn-mcp-v2/config.json` |
| Logs | Inside VS output window | `%LOCALAPPDATA%\RoslynMcp\logs\` |
| Requires VS running | Yes | No |

## Before You Switch

Complete these steps in order. Do not remove the v1 extension until you have verified v2 works.

1. **Export knowledge base entries** from v1 (these cannot be auto-reconstructed):
   ```
   # Via v1 MCP, call kb_search with empty query to list all entries
   # Save the output — you will re-import into v2 via kb_add
   ```

2. **Export memory entries** from v1:
   ```
   # Via v1 MCP, call memory_export
   # Save the JSON output to a file (e.g., memory-export.json)
   ```

3. **Note your v1 config path**:
   ```
   # Default location (in your solution directory):
   <solution-dir>/.roslyn-mcp/config.json
   ```

4. **Build the v2 server** (if not already published):
   ```bash
   cd Solution/Tools/RoslynMcp
   dotnet publish src/RoslynMcp.Server -c Release --self-contained -r win-x64
   ```

5. **Add v2 MCP config** to your project `.mcp.json` (or `~/.claude.json` for global):
   ```json
   "roslyn-v2": {
     "type": "stdio",
     "command": "<path-to>/RoslynMcp.Server.exe",
     "args": ["--solution-path", "<path-to>/YourSolution.sln"],
     "timeout": 120000
   }
   ```

6. **First launch** — start Claude Code. The v2 server will:
   - Load the solution via MSBuild (expect 15-30s for large solutions)
   - Create `.roslyn-mcp-v2/` in the solution directory
   - Run database migrations (creates tables automatically)
   - Register all 94 tools

7. **Verify** by calling `get_workspace_status` — confirm project count and `isFullyLoaded: true`.

8. **Import config** from v1 (optional):
   ```bash
   RoslynMcp.Server.exe --solution-path <sln> --import-from <solution-dir>/.roslyn-mcp/config.json
   ```
   This copies known settings (tool enabled states, timeouts) into v2 format. Unknown keys are skipped with a warning. A backup of any existing v2 config is created automatically.

9. **Import memory** into v2:
   ```
   # Via v2 MCP, call memory_import with the JSON saved in step 2
   ```

10. **Re-create KB entries** in v2:
    ```
    # Via v2 MCP, call kb_add for each entry saved in step 1
    ```

11. **Disable v1** — remove or rename the `roslyn` entry in your MCP config. Keep the extension installed as a fallback until you are confident in v2.

## What Transfers Automatically

**Config settings** — via `--import-from`:
- Tool enabled/disabled states (`tool.<name>.enabled`)
- Per-tool timeout overrides (`timeout.<name>`)
- Known global settings (`max_page_size`, `timeout.default`)
- Schema version is set to `"2.0"` in the imported config
- Unknown keys (v1-specific or deprecated) are logged at WARN and skipped
- Invalid values are logged at ERROR and fall back to defaults
- A pre-import backup is created if v2 config already exists

Running import twice without `--force` is a no-op (idempotent).

## What Rebuilds on First Use

**Dependency graph** — the v2 server derives the dependency graph from the workspace. Call `graph_rebuild` after first launch (or it rebuilds automatically on first graph query). No data is lost because the graph is computed from project references, not user-authored.

**Database schema** — migrations run automatically on first launch. Tables for sessions, memory entries, graph nodes/edges, and knowledge base are created fresh.

## What Needs Manual Re-Creation

**Knowledge base entries** — these are user-authored notes, tags, and documentation. There is no automatic migration path because the v1 and v2 KB schemas differ. Export from v1 before switching and re-import into v2 via the `kb_add` tool.

**Memory entries** — session-scoped memory (store/retrieve/search). Use `memory_export` on v1 to get JSON, then `memory_import` on v2. Compatible fields transfer; incompatible fields are logged at WARN and skipped.

**Custom graph nodes/edges** — if you manually added nodes or edges (beyond the auto-built dependency graph), these do not transfer. Re-add them via `graph_add_node` / `graph_add_edge`.

## Rollback

If v2 does not work as expected:

1. Edit your MCP config: remove `roslyn-v2`, restore `roslyn`
2. Restart Claude Code
3. The original `.roslyn-mcp/` data is untouched — v2 never writes to it
4. Optionally delete `.roslyn-mcp-v2/` to reclaim disk space

The v2 server's data directory (`.roslyn-mcp-v2/`) is completely independent from v1's `.roslyn-mcp/`. No v1 data is modified, moved, or deleted by any v2 operation.

## Environment Variables

| Variable | Purpose | Default |
|----------|---------|---------|
| `ROSLYNMCP_SOLUTION_PATH` | Solution path (alternative to `--solution-path`) | none |
| `ROSLYNMCP_LOG_DIR` | Log file directory | `%LOCALAPPDATA%\RoslynMcp\logs` |

## CLI Reference

```
RoslynMcp.Server.exe [options]

  --solution-path <path>   Path to .sln file (required)
  --msbuild-path <path>    Path to MSBuild (auto-detected if omitted)
  --import-from <path>     Import config from v1 config.json
  --force                  Overwrite existing v2 config during import
  --warm-up                Pre-compile all projects after loading
```
