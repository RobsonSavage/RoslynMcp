# Roslyn Workspace Follow for Codex

This plugin calls the connected `roslyn` MCP server's `set_solution_root` tool before every
Roslyn MCP tool call. It uses the `cwd` reported by the Codex hook event.

The RoslynMcp server must already be configured under the name `roslyn`. Codex requires review and
trust before it runs an installed plugin hook. An explicit `--solution-path`,
`ROSLYNMCP_SOLUTION_PATH`, or successful manual `set_solution_path` call keeps its pinned solution
and makes the hook call a no-op.

Install the repository marketplace, then install the plugin:

```text
codex plugin marketplace add https://github.com/RobsonSavage/RoslynMcp
codex plugin add roslyn-workspace-follow@roslyn-mcp
```

Start a new Codex thread after installation.
