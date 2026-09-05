Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Assert-True {
    param(
        [Parameter(Mandatory)][bool]$Condition,
        [Parameter(Mandatory)][string]$Message
    )

    if (-not $Condition) {
        throw "Assertion failed: $Message"
    }
}

function Invoke-ConfigScript {
    param(
        [Parameter(Mandatory)][string]$ScriptPath,
        [Parameter(Mandatory)][string]$ProfilePath
    )

    $parameters = @{
        UserProfilePath = $ProfilePath
        SkipUserEnvironment = $true
    }
    & $ScriptPath @parameters
}

$repoRoot = Split-Path -Parent $PSScriptRoot
$scriptPath = Join-Path $repoRoot "configure-clients.ps1"
$artifactsRoot = [IO.Path]::GetFullPath((Join-Path $repoRoot "artifacts"))
$testRoot = Join-Path $artifactsRoot "configure-clients-$([guid]::NewGuid().ToString('N'))"
$profilePath = Join-Path $testRoot "existing"
$newProfilePath = Join-Path $testRoot "new"
$plainProfilePath = Join-Path $testRoot "plain"
$duplicateKeyProfilePath = Join-Path $testRoot "duplicate-keys"
$invalidProfilePath = Join-Path $testRoot "invalid"
$oldCodexHome = $env:CODEX_HOME

try {
    [void](New-Item -ItemType Directory -Path (Join-Path $profilePath ".codex") -Force)

    $claudeFixture = @'
{
  "theme": "dark",
  "mcpServers": {
    "other": {
      "type": "stdio",
      "command": "other-server"
    },
    "roslyn": {
      "type": "stdio",
      "command": "cmd",
      "args": [
        "/c",
        "%LOCALAPPDATA%\\RoslynMcp\\RoslynMcp.Server.exe"
      ],
      "env": {
        "EXISTING_VAR": "keep",
        "ROSLYNMCP_BOOTSTRAP_SOLUTION_PATH": "${ROSLYNMCP_BOOTSTRAP_SOLUTION_PATH}"
      },
      "timeout": 120000
    }
  },
  "projects": {
    "C:\\sample": {}
  }
}
'@
    [IO.File]::WriteAllText(
        (Join-Path $profilePath ".claude.json"),
        $claudeFixture,
        [Text.UTF8Encoding]::new($false))

    $codexFixture = @'
model = "test"

[mcp_servers.roslyn]
command = "cmd"
args = ["/c", "%LOCALAPPDATA%\\RoslynMcp\\RoslynMcp.Server.exe"]
env_vars = ["EXISTING_VAR", "ROSLYNMCP_BOOTSTRAP_SOLUTION_PATH"]
tool_timeout_sec = 60
startup_timeout_sec = 120

[mcp_servers.other]
command = "other-server"
'@
    [IO.File]::WriteAllText(
        (Join-Path $profilePath ".codex\config.toml"),
        $codexFixture,
        [Text.UTF8Encoding]::new($false))

    Invoke-ConfigScript -ScriptPath $scriptPath -ProfilePath $profilePath

    $claudePath = Join-Path $profilePath ".claude.json"
    $claude = Get-Content -Raw -LiteralPath $claudePath | ConvertFrom-Json
    Assert-True ($claude.theme -eq "dark") "Claude root settings are preserved"
    Assert-True ($claude.mcpServers.other.command -eq "other-server") "Other Claude MCP servers are preserved"
    Assert-True ($claude.mcpServers.roslyn.env.EXISTING_VAR -eq "keep") "Other Claude MCP environment values are preserved"
    Assert-True (
        $null -eq $claude.mcpServers.roslyn.env.PSObject.Properties["ROSLYNMCP_BOOTSTRAP_SOLUTION_PATH"]
    ) "Claude bootstrap environment forwarding is removed"

    $codexPath = Join-Path $profilePath ".codex\config.toml"
    $codexText = [IO.File]::ReadAllText($codexPath)
    Assert-True ($codexText.Contains('"EXISTING_VAR"')) "Existing Codex env_vars are preserved"
    Assert-True (
        -not $codexText.Contains('"ROSLYNMCP_BOOTSTRAP_SOLUTION_PATH"')
    ) "Codex bootstrap whitelist is removed"

    $codex = Get-Command codex -ErrorAction SilentlyContinue
    if ($null -ne $codex) {
        $env:CODEX_HOME = Join-Path $profilePath ".codex"
        $effective = & $codex.Source mcp get roslyn --json | ConvertFrom-Json
        Assert-True ($effective.transport.env_vars -contains "EXISTING_VAR") "Codex parses the preserved whitelist"
    }

    $claudeHash = (Get-FileHash -LiteralPath $claudePath -Algorithm SHA256).Hash
    $codexHash = (Get-FileHash -LiteralPath $codexPath -Algorithm SHA256).Hash
    Invoke-ConfigScript -ScriptPath $scriptPath -ProfilePath $profilePath
    Assert-True (
        (Get-FileHash -LiteralPath $claudePath -Algorithm SHA256).Hash -eq $claudeHash
    ) "Claude configuration is idempotent"
    Assert-True (
        (Get-FileHash -LiteralPath $codexPath -Algorithm SHA256).Hash -eq $codexHash
    ) "Codex configuration is idempotent"

    Invoke-ConfigScript -ScriptPath $scriptPath -ProfilePath $newProfilePath
    $newClaude = Get-Content -Raw -LiteralPath (Join-Path $newProfilePath ".claude.json") | ConvertFrom-Json
    Assert-True ($newClaude.mcpServers.roslyn.command -eq "cmd") "Missing Claude configuration is created"
    Assert-True ($null -eq $newClaude.mcpServers.roslyn.PSObject.Properties["env"]) "New Claude entry has no bootstrap environment"
    $newCodex = [IO.File]::ReadAllText((Join-Path $newProfilePath ".codex\config.toml"))
    Assert-True ($newCodex.Contains("[mcp_servers.roslyn]")) "Missing Codex configuration is created"
    Assert-True (-not $newCodex.Contains("env_vars")) "New Codex entry has no bootstrap whitelist"

    [void](New-Item -ItemType Directory -Path (Join-Path $plainProfilePath ".codex") -Force)
    [IO.File]::WriteAllText(
        (Join-Path $plainProfilePath ".claude.json"),
        '{ "theme": "light" }',
        [Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllText(
        (Join-Path $plainProfilePath ".codex\config.toml"),
        'model = "test"',
        [Text.UTF8Encoding]::new($false))
    Invoke-ConfigScript -ScriptPath $scriptPath -ProfilePath $plainProfilePath
    $plainClaude = Get-Content -Raw -LiteralPath (Join-Path $plainProfilePath ".claude.json") | ConvertFrom-Json
    Assert-True ($plainClaude.theme -eq "light") "Claude settings survive adding mcpServers"
    Assert-True ($plainClaude.mcpServers.roslyn.command -eq "cmd") "Claude mcpServers is added"
    $plainCodex = [IO.File]::ReadAllText((Join-Path $plainProfilePath ".codex\config.toml"))
    Assert-True ($plainCodex.Contains('model = "test"')) "Codex settings survive adding the server"
    Assert-True ($plainCodex.Contains("[mcp_servers.roslyn]")) "Codex roslyn section is added"

    [void](New-Item -ItemType Directory -Path (Join-Path $duplicateKeyProfilePath ".codex") -Force)
    $duplicateKeyFixture = @'
{
  "projects": {},
  "Projects": {},
  "mcpServers": {
    "roslyn": {
      "type": "stdio",
      "command": "cmd",
      "env": {
        "ROSLYNMCP_BOOTSTRAP_SOLUTION_PATH": "${ROSLYNMCP_BOOTSTRAP_SOLUTION_PATH}"
      }
    }
  }
}
'@
    $duplicateClaudePath = Join-Path $duplicateKeyProfilePath ".claude.json"
    [IO.File]::WriteAllText($duplicateClaudePath, $duplicateKeyFixture, [Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllText(
        (Join-Path $duplicateKeyProfilePath ".codex\config.toml"),
        'model = "test"',
        [Text.UTF8Encoding]::new($false))
    Invoke-ConfigScript -ScriptPath $scriptPath -ProfilePath $duplicateKeyProfilePath
    $duplicateResult = [IO.File]::ReadAllText($duplicateClaudePath)
    Assert-True ($duplicateResult.Contains('"projects"')) "Lower-case Claude root key is preserved"
    Assert-True ($duplicateResult.Contains('"Projects"')) "Case-distinct Claude root key is preserved"
    Assert-True (
        -not $duplicateResult.Contains('"ROSLYNMCP_BOOTSTRAP_SOLUTION_PATH"')
    ) "Claude MCP block is cleaned without parsing unrelated root keys"

    [void](New-Item -ItemType Directory -Path (Join-Path $invalidProfilePath ".codex") -Force)
    $invalidClaudePath = Join-Path $invalidProfilePath ".claude.json"
    $invalidCodexPath = Join-Path $invalidProfilePath ".codex\config.toml"
    [IO.File]::WriteAllText($invalidClaudePath, '{ invalid', [Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllText($invalidCodexPath, 'model = "untouched"', [Text.UTF8Encoding]::new($false))
    $failed = $false
    try {
        Invoke-ConfigScript -ScriptPath $scriptPath -ProfilePath $invalidProfilePath
    }
    catch {
        $failed = $true
    }
    Assert-True $failed "Invalid Claude JSON stops configuration"
    Assert-True (
        [IO.File]::ReadAllText($invalidCodexPath) -eq 'model = "untouched"'
    ) "A failed Claude update leaves Codex untouched"

    Write-Output "configure-clients tests passed"
}
finally {
    $env:CODEX_HOME = $oldCodexHome
    $resolvedTestRoot = [IO.Path]::GetFullPath($testRoot)
    $requiredPrefix = $artifactsRoot.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    if (-not $resolvedTestRoot.StartsWith($requiredPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to clean a test directory outside artifacts: $resolvedTestRoot"
    }
    if (Test-Path -LiteralPath $resolvedTestRoot) {
        Remove-Item -LiteralPath $resolvedTestRoot -Recurse -Force
    }
}
