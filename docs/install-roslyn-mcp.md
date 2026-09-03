# RoslynMcp Installation Guide

This guide covers installing RoslynMcp.Server for use with Claude Code, Codex and OpenCode,
including support for git worktrees and multiple simultaneous sessions.

## Overview

RoslynMcp.Server provides Roslyn-powered code analysis tools through the Model Context Protocol
(MCP). The recommended setup uses:

- **User-local installation** for the binaries (works across all repos)
- **Startup discovery** from the client's working directory
- **A client hook plugin** for Claude Code or Codex when a session moves to another worktree

An explicit `--solution-path` or `ROSLYNMCP_SOLUTION_PATH` is a pin. It disables client workspace
following for that server process.

`ROSLYNMCP_BOOTSTRAP_SOLUTION_PATH` is the opposite: a solution to start on when discovery finds
none, which leaves following enabled. Without it a session started outside any solution kills the
server at startup, and the tool that would have fixed it does not exist yet.

---

## Prerequisites

- .NET 10 SDK (or .NET runtime if using self-contained deployment)
- Claude Code CLI
- Built against `ModelContextProtocol` 2.0.0, which accepts four MCP spec revisions through the `initialize` handshake (`2024-11-05`, `2025-03-26`, `2025-06-18`, `2025-11-25`); `2026-07-28` is reached via `server/discover`, not `initialize`. A client proposing a revision newer than the handshake set gets `-32022` with the supported list in `error.data`, and must retry with one of them
- PowerShell (for wrapper script)
- Git (for worktree support)

---

## Step 1: User-Local Installation

### Create Publish Script

Create `publish-local.ps1` in `Solution/Tools/RoslynMcp/`:

```powershell
# Build and publish to user-local directory
$publishDir = Join-Path $env:LOCALAPPDATA "RoslynMcp"
$projectPath = "src\RoslynMcp.Server\RoslynMcp.Server.csproj"

Write-Host "Building RoslynMcp.Server (Release)..."
dotnet publish $projectPath `
    -c Release `
    -o $publishDir `
    --self-contained false `
    -p:PublishSingleFile=false

if ($LASTEXITCODE -eq 0) {
    Write-Host "Published to: $publishDir" -ForegroundColor Green
    Write-Host "`nExecutable location:" -ForegroundColor Cyan
    Write-Host "  $publishDir\RoslynMcp.Server.exe"
} else {
    Write-Host "Build failed!" -ForegroundColor Red
    exit 1
}
```

### Run Initial Publish

From the RoslynMcp clone root:

```powershell
.\publish-local.ps1
```

This installs RoslynMcp.Server to:
```
%LOCALAPPDATA%\RoslynMcp\
├── RoslynMcp.Server.exe
├── RoslynMcp.Core.dll
├── RoslynMcp.Shared.dll
└── ... (all dependencies)
```

**Typical path**: `%LOCALAPPDATA%\RoslynMcp\`

---

## Step 2: Configure the MCP Server Without a Solution Pin

Configure the server under the name `roslyn`. Launch the installed executable without
`--solution-path`, and make sure `ROSLYNMCP_SOLUTION_PATH` is not set. The server discovers the
solution from the client process working directory at startup.

For Claude Code, add or update the `roslyn` entry in `~/.claude.json`:

```json
{
  "mcpServers": {
    "roslyn": {
      "type": "stdio",
      "command": "powershell.exe",
      "args": [
        "-NoProfile",
        "-Command",
        "& (Join-Path $env:LOCALAPPDATA 'RoslynMcp\\RoslynMcp.Server.exe')"
      ],
      "timeout": 120000
    }
  }
}
```

Restart the client after changing MCP configuration.

## Step 3: Install Workspace-Following Support

The server returns MCP instructions asking clients to call `set_solution_root` before their first
Roslyn call and after a workspace move. Claude Code and Codex can also install deterministic hook
plugins from this repository.

### Claude Code

```text
/plugin marketplace add RobsonSavage/RoslynMcp
/plugin install roslyn-workspace-follow@roslyn-mcp
```

The plugin handles `CwdChanged` and also calls `set_solution_root(cwd)` before every Roslyn tool.
The `PreToolUse` fallback is required because `EnterWorktree` does not emit `CwdChanged` in Claude
Code 2.1.259. Restart Claude Code after installation. The MCP server must be named `roslyn`.

### Codex

```text
codex plugin marketplace add RobsonSavage/RoslynMcp
codex plugin add roslyn-workspace-follow@roslyn-mcp
```

Review and trust the installed hook, then start a new thread. Codex has no `CwdChanged` event, so
the plugin calls `set_solution_root(cwd)` synchronously before every `mcp__roslyn__*` tool call.

### OpenCode

OpenCode 1.18.27 reads RoslynMcp's server instructions into its system prompt. It does not expose a
plugin API that can invoke a tool on an existing MCP connection, so workspace following remains an
agent instruction: call `set_solution_root` before the first Roslyn call and after entering another
worktree. Starting a new OpenCode session inside each worktree remains the deterministic option.

## Pinned Fallback: Wrapper Script Per Session

Use the wrapper flow only when each worktree gets a separate client session and server process. It
passes `--solution-path`, so the workspace-following plugins intentionally cannot override it.

### Create Wrapper Script

Create `start-roslyn-mcp.ps1` in the repository root:

```powershell
# Auto-detect solution file in current directory tree
param(
    [string]$WorkingDir = $PWD.Path
)

$logPath = Join-Path $env:TEMP "roslyn-mcp-wrapper.log"
"[$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')] Starting from: $WorkingDir" | Out-File $logPath -Append

# Search up the directory tree for *.sln
$currentDir = $WorkingDir
$solutionPath = $null

while ($currentDir) {
    $slnFiles = Get-ChildItem -Path $currentDir -Filter "*.sln" -ErrorAction SilentlyContinue
    if ($slnFiles) {
        $solutionPath = $slnFiles[0].FullName
        "[$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')] Found solution: $solutionPath" | Out-File $logPath -Append
        break
    }

    $parent = Split-Path $currentDir -Parent
    if ($parent -eq $currentDir) { break }  # Reached root
    $currentDir = $parent
}

if (-not $solutionPath) {
    # Fallback: solution relative to this script's location (repo root)
    $solutionPath = Join-Path $PSScriptRoot "Solution\QMaster.sln"
    "[$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')] No solution found, using fallback: $solutionPath" | Out-File $logPath -Append
}

# Launch server from user-local installation
$serverExe = Join-Path $env:LOCALAPPDATA "RoslynMcp\RoslynMcp.Server.exe"

if (-not (Test-Path $serverExe)) {
    "[$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')] ERROR: Server not found at $serverExe" | Out-File $logPath -Append
    Write-Error "RoslynMcp.Server not found. Run publish-local.ps1 first."
    exit 1
}

"[$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')] Launching: $serverExe --solution-path `"$solutionPath`"" | Out-File $logPath -Append

& $serverExe --solution-path $solutionPath
```

The fallback path is derived from `$PSScriptRoot`, so it works regardless of where the repo is cloned.

---

## Configure Claude Code With the Pinned Wrapper

### Update `~/.claude.json`

Add or update the `roslyn` MCP server configuration:

```json
{
  "mcpServers": {
    "roslyn": {
      "type": "stdio",
      "command": "powershell.exe",
      "args": [
        "-ExecutionPolicy", "Bypass",
        "-NoProfile",
        "-File", "start-roslyn-mcp.ps1"
      ],
      "timeout": 120000
    }
  }
}
```

**Adjust the path** to `start-roslyn-mcp.ps1` to match where you saved it.

### Restart Claude Code

```bash
# Exit all Claude Code instances
# Relaunch Claude Code
ccq
```

---

## Configure CLAUDE.md

Claude Code needs instructions telling it to prefer Roslyn tools over text-based search. Without this, it defaults to Grep/Glob for `.cs` files — losing semantic accuracy.

### What to Add

Add a `## RoslynMCP` section to your `CLAUDE.md`:

| Location | When |
|----------|------|
| `<repo>/CLAUDE.md` | RoslynMCP used for one project (shared with team via git) |
| `~/.claude/CLAUDE.md` | RoslynMCP used across multiple projects |

If both exist, both load — avoid contradictions.

### Required Content

```markdown
## RoslynMCP

When the Roslyn MCP server is connected:
- **Find tools**: Use `ToolSearch` with `+roslyn` prefix (e.g., `+roslyn find callers`) to discover Roslyn MCP tools
- **C# code analysis**: Never use Grep/Glob for .cs files — use Roslyn tools only (semantic accuracy vs text matching)
- **Workspace scope**: Roslyn loads `<YourSolution>.sln` only. Sub-solutions are NOT included. A search returning 0 results may mean the code is outside the loaded solution — verify with `get_workspace_status` before falling back to Grep.
- **Workspace changes**: Before the first Roslyn call and after changing directory or entering a worktree, call `set_solution_root` with the absolute current workspace directory unless the server reports that following is disabled.
- **Never assume Roslyn is broken from one failed search.** Always confirm with `get_workspace_status` or a known-good query before switching to Grep.
- **Memory layers**:
  - `memory_store(key, value)` / `memory_search` / `memory_retrieve` — context & rules. Omit `sessionId` for global, include for session-scoped.
  - `kb_add` / `kb_search` / `kb_get` — persistent knowledge base
  - `graph_*` — dependency tracking, impact analysis, change relationships
- **Session start**: Call `memory_search` to restore prior context
```

### Why Each Rule Matters

| Rule | Without it |
|------|-----------|
| `+roslyn` prefix for ToolSearch | Claude guesses tool names or uses keyword search, wasting turns |
| Never Grep/Glob for .cs | Falls back to text matching — misses overloads, partial classes, extension methods |
| Workspace scope warning | Returns 0 results for code in sub-solutions, Claude assumes "not found" |
| Verify before fallback | One timeout or empty result triggers permanent Grep fallback for the session |
| Memory layers | Claude ignores persistent context, re-discovers the same patterns every session |

### Optional: Memory Bootstrap Rule

If you use Roslyn memory to persist architectural decisions or coding rules across sessions, add:

```markdown
- **Session start**: Call `memory_search` with `query: "rules"` to restore prior context before beginning work
```

This causes Claude to reload stored rules (e.g., "always use Result<T>", "no async void") at the start of each conversation.

---

## Step 5: Verify Installation

### Check the Wrapper Log

```powershell
cat $env:TEMP\roslyn-mcp-wrapper.log
```

You should see `Starting from`, `Found solution` and `Launching` entries for the current worktree,
with the executable under `%LOCALAPPDATA%\RoslynMcp`.

### Check Running Process

```powershell
Get-Process RoslynMcp.Server -ErrorAction SilentlyContinue
```

If the server is running, you'll see:
```
 NPM(K)    PM(M)      WS(M)     CPU(s)      Id  SI ProcessName
 ------    -----      -----     ------      --  -- -----------
      0     0.00     123.45       1.23   12345   1 RoslynMcp.Server
```

### Test with Claude Code

In Claude Code, try:
```
@roslyn get_solution_structure
```

You should receive solution information for your current worktree.

---

## Usage with Git Worktrees

### Create Multiple Worktrees

```bash
# Create feature worktree
git worktree add ..\qmaster-feature feature-branch

# Create hotfix worktree
git worktree add ..\qmaster-hotfix hotfix-branch
```

### Launch Claude Code in Each Worktree

**Terminal 1 (main checkout):**
```bash
cc
```

**Terminal 2 (feature):**
```bash
cd ..\qmaster-feature
cc
```

**Terminal 3 (hotfix):**
```bash
cd ..\qmaster-hotfix
cc
```

### How It Works

Each separately launched client instance:
1. Spawns its own `RoslynMcp.Server.exe` process
2. Startup discovery finds the `.sln` or `.slnx` file in the current worktree
3. The server loads that specific workspace
4. Data is stored in `.roslyn-mcp-data/` within each worktree (separate SQLite DBs)

When an existing session moves between worktrees, the Claude Code and Codex plugins call
`set_solution_root`. The switch includes the workspace, configuration and per-solution SQLite
database, so memory, KB and graph results change to the target solution's scope.

### Verify Multiple Instances

```powershell
Get-Process RoslynMcp.Server | Format-Table Id, StartTime, @{
    L="CommandLine";
    E={(Get-CimInstance Win32_Process -Filter "ProcessId=$($_.Id)").CommandLine}
}
```

You should see multiple processes with different `--solution-path` arguments.

---

## Updating RoslynMcp

Run every command from the root of your clone - `publish-local.ps1` resolves the project
by relative path, so the working directory matters.

```powershell
# 1. Get the change
git pull

# 2. Close Claude Code, then confirm nothing still holds the exe
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

# 5. Republish
.\publish-local.ps1

# 6. Verify (see Check Version below)

# 7. Restart all Claude Code instances
```

Steps 2 and 4 are not optional:

- **Step 2**: with Claude Code open the exe is locked, `dotnet publish` fails partway, and you
  are left with new DLLs beside an old exe. That fails later, in confusing ways, rather than now.
- **Step 4**: `dotnet publish -o` overwrites same-named files but never deletes orphans, so install
  directories accumulate stale assemblies across upgrades.

If step 5 fails, roll back with
`Remove-Item $d -Recurse -Force; Rename-Item "$d.bak" $d`. Delete the `.bak` once step 6 looks right.

Note that the `Microsoft.Extensions.Hosting 10.0.10` floor requires the .NET 10 SDK. An older SDK
fails at restore.

### Check Version

The server has no `--version` flag; it always resolves a solution first and exits non-zero if it
cannot. Read the assembly versions from the install directory instead:

```powershell
$d = Join-Path $env:LOCALAPPDATA 'RoslynMcp'
Get-ChildItem $d -Filter 'ModelContextProtocol*.dll' |
    Select-Object Name, @{n='Version';e={$_.VersionInfo.ProductVersion}}
```

Both entries must read `2.0.0`. A `0.8.0-preview.1` here means the install is stale regardless of
what `git log` says, because the running server loads from this directory, not from your clone.

---

## Troubleshooting

### Server Not Starting

**Check the wrapper log:**
```powershell
cat $env:TEMP\roslyn-mcp-wrapper.log
```

**Common issues:**
- Server not found: Run `publish-local.ps1` to install
- Solution not found: Check the fallback path in the wrapper script
- MSBuild not found: Install Visual Studio or VS Build Tools

### Wrong Solution Loaded

**Check which solution was detected:**
```powershell
cat $env:TEMP\roslyn-mcp-wrapper.log | Select-String "Found solution"
```

**Fix:**
- Ensure you're running Claude Code from the correct worktree directory
- Check that a `.sln` file exists in the directory tree
- Check for `--solution-path` or `ROSLYNMCP_SOLUTION_PATH`; either one deliberately disables following
- If the server did not start at all, check the working directory holds a solution, or set `ROSLYNMCP_BOOTSTRAP_SOLUTION_PATH`
- Confirm the workspace-follow plugin is installed, enabled and trusted, then call `get_workspace_status`
- Call `set_solution_root` with the absolute worktree directory to test the server independently of the client hook

### Multiple Instances Conflict

**Check for port conflicts:**
```powershell
Get-Process RoslynMcp.Server | Measure-Object | Select-Object Count
```

**Note:** Multiple instances are expected with worktrees. They use stdio (no port conflicts) and separate data directories.

### Performance Issues

If the server is slow to start:
- Use the `--warm-up` flag in the wrapper script (adds ~10-30s to startup for full compilation)
- Ensure your solution builds successfully
- Check for large numbers of projects (>100 can be slow)

---

## Advanced Configuration

### Self-Contained Deployment

For portability without requiring .NET runtime:

```powershell
dotnet publish $projectPath `
    -c Release `
    -o $publishDir `
    --self-contained true `
    -r win-x64 `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true
```

Creates a single ~80MB executable with all dependencies bundled.

### Warm-Up on Startup

Pre-compile the solution for faster subsequent tool calls:

**In wrapper script:**
```powershell
& $serverExe --solution-path $solutionPath --warm-up
```

**Trade-off:** Adds 10-30 seconds to server startup, but makes all tools 2-5x faster.

---

## Team Deployment

For team-wide deployment, use a shared network location instead of user-local:

### Publish to Network Share

```powershell
$publishDir = "\\server\shared\tools\RoslynMcp"
dotnet publish $projectPath -c Release -o $publishDir --self-contained false
```

### Update Wrapper Script

```powershell
$serverExe = "\\server\shared\tools\RoslynMcp\RoslynMcp.Server.exe"
```

### Benefits
- Entire team uses the same version
- Single update point for administrators
- Consistent behavior across team members

---

## See Also

- [MIGRATION.md](MIGRATION.md) - Migrating from RoslynMcp v1 (VS extension)
- [QM-4351-progress.md](../../../QM-4351-progress.md) - Development progress
- [MCP Protocol Documentation](https://modelcontextprotocol.io/)
