# Roslyn Workspace Follow for Claude Code

This plugin calls the connected `roslyn` MCP server's `set_solution_root` tool whenever Claude Code
emits `CwdChanged` and before each Roslyn MCP tool call. The `PreToolUse` hook is required because
`EnterWorktree` changes the session directory without emitting `CwdChanged` in Claude Code 2.1.259.
The RoslynMcp server must already be configured under the name `roslyn`.

An explicit `--solution-path`, `ROSLYNMCP_SOLUTION_PATH`, or successful manual
`set_solution_path` call keeps its pinned solution and makes the hook call a no-op.

Install the repository marketplace, then install the plugin:

```text
/plugin marketplace add RobsonSavage/RoslynMcp
/plugin install roslyn-workspace-follow@roslyn-mcp
```

Restart Claude Code after installation.
