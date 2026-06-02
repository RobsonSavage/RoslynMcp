# RoslynMcp Installation Guide

This guide covers installing RoslynMcp.Server for use with Claude Code, including support for git worktrees and multiple simultaneous sessions.

## Overview

RoslynMcp.Server provides Roslyn-powered code analysis tools to Claude Code via the Model Context Protocol (MCP). The recommended setup uses:
- **User-local installation** for the binaries (works across all repos)
- **Wrapper script with auto-detection** to find the solution in your current worktree

This configuration allows you to run multiple Claude Code instances simultaneously, each analyzing a different worktree.

---

## Prerequisites

- .NET 10 SDK (or .NET runtime if using self-contained deployment)
- Claude Code CLI
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

```powershell
cd P:\qmaster\Solution\Tools\RoslynMcp
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

**Typical path**: `C:\Users\<username>\AppData\Local\RoslynMcp\`

---

## Step 2: Wrapper Script with Auto-Detection

The wrapper script automatically detects which solution to load based on your current working directory.

### Create Wrapper Script

Create `start-roslyn-mcp.ps1` in the repo root (e.g., `P:\qmaster\`):

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

## Step 3: Configure Claude Code

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
        "-File", "P:\\qmaster\\start-roslyn-mcp.ps1"
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

## Step 4: Configure CLAUDE.md

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

You should see entries like:
```
[2026-02-10 14:30:00] Starting from: P:\qmaster
[2026-02-10 14:30:00] Found solution: P:\qmaster\Solution\QMaster.sln
[2026-02-10 14:30:00] Launching: C:\Users\dave\AppData\Local\RoslynMcp\RoslynMcp.Server.exe --solution-path "P:\qmaster\Solution\QMaster.sln"
```

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
# Main worktree
cd P:\qmaster

# Create feature worktree
git worktree add ..\qmaster-feature feature-branch

# Create hotfix worktree
git worktree add ..\qmaster-hotfix hotfix-branch
```

### Launch Claude Code in Each Worktree

**Terminal 1 (main):**
```bash
cd P:\qmaster
cc
```

**Terminal 2 (feature):**
```bash
cd P:\qmaster-feature
cc
```

**Terminal 3 (hotfix):**
```bash
cd P:\qmaster-hotfix
cc
```

### How It Works

Each Claude Code instance:
1. Spawns its own `RoslynMcp.Server.exe` process
2. The wrapper script detects the `.sln` file in the current worktree
3. The server loads that specific workspace
4. Data is stored in `.roslyn-mcp-data/` within each worktree (separate SQLite DBs)

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

When you pull server changes from git:

```powershell
cd P:\qmaster\Solution\Tools\RoslynMcp
.\publish-local.ps1
```

Then restart all Claude Code instances to pick up the new version.

### Check Version

```powershell
& "$env:LOCALAPPDATA\RoslynMcp\RoslynMcp.Server.exe" --version
```

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
- Verify the fallback path in the wrapper script

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

### Custom Data Directory

Override the default `.roslyn-mcp-data/` location:

**In wrapper script, add:**
```powershell
$env:ROSLYNMCP_DATA_DIR = "C:\temp\roslyn-cache"
& $serverExe --solution-path $solutionPath
```

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
