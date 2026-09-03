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
        [Parameter(Mandatory)][string]$ProfilePath,
        [Parameter(Mandatory)][string]$SolutionPath
    )

    $parameters = @{
        BootstrapSolutionPath = $SolutionPath
        UserProfilePath = $ProfilePath
        SkipUserEnvironment = $true
    }
    & $ScriptPath @parameters
}

$repoRoot = Split-Path -Parent $PSScriptRoot
$scriptPath = Join-Path $repoRoot "configure-clients.ps1"
$solutionPath = Join-Path $repoRoot "RoslynMcp.sln"
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
env_vars = ["EXISTING_VAR"]
tool_timeout_sec = 60
startup_timeout_sec = 120

[mcp_servers.other]
command = "other-server"
'@
    [IO.File]::WriteAllText(
        (Join-Path $profilePath ".codex\config.toml"),
        $codexFixture,
        [Text.UTF8Encoding]::new($false))

    Invoke-ConfigScript -ScriptPath $scriptPath -ProfilePath $profilePath -SolutionPath $solutionPath

    $claudePath = Join-Path $profilePath ".claude.json"
    $claude = Get-Content -Raw -LiteralPath $claudePath | ConvertFrom-Json
    Assert-True ($claude.theme -eq "dark") "Claude root settings are preserved"
    Assert-True ($claude.mcpServers.other.command -eq "other-server") "Other Claude MCP servers are preserved"
    $expectedReference = '$' + '{ROSLYNMCP_BOOTSTRAP_SOLUTION_PATH}'
    Assert-True (
        $claude.mcpServers.roslyn.env.ROSLYNMCP_BOOTSTRAP_SOLUTION_PATH -eq $expectedReference
    ) "Claude forwards the bootstrap environment variable"

    $codexPath = Join-Path $profilePath ".codex\config.toml"
    $codexText = [IO.File]::ReadAllText($codexPath)
    Assert-True ($codexText.Contains('"EXISTING_VAR"')) "Existing Codex env_vars are preserved"
    Assert-True ($codexText.Contains('"ROSLYNMCP_BOOTSTRAP_SOLUTION_PATH"')) "Codex bootstrap whitelist is added"
    Assert-True (
        [regex]::Matches($codexText, '"ROSLYNMCP_BOOTSTRAP_SOLUTION_PATH"').Count -eq 1
    ) "Codex bootstrap whitelist is unique"

    $codex = Get-Command codex -ErrorAction SilentlyContinue
    if ($null -ne $codex) {
        $env:CODEX_HOME = Join-Path $profilePath ".codex"
        $effective = & $codex.Source mcp get roslyn --json | ConvertFrom-Json
        Assert-True (
            $effective.transport.env_vars -contains "ROSLYNMCP_BOOTSTRAP_SOLUTION_PATH"
        ) "Codex parses the generated whitelist"
    }

    $claudeHash = (Get-FileHash -LiteralPath $claudePath -Algorithm SHA256).Hash
    $codexHash = (Get-FileHash -LiteralPath $codexPath -Algorithm SHA256).Hash
    Invoke-ConfigScript -ScriptPath $scriptPath -ProfilePath $profilePath -SolutionPath $solutionPath
    Assert-True (
        (Get-FileHash -LiteralPath $claudePath -Algorithm SHA256).Hash -eq $claudeHash
    ) "Claude configuration is idempotent"
    Assert-True (
        (Get-FileHash -LiteralPath $codexPath -Algorithm SHA256).Hash -eq $codexHash
    ) "Codex configuration is idempotent"

    Invoke-ConfigScript -ScriptPath $scriptPath -ProfilePath $newProfilePath -SolutionPath $solutionPath
    $newClaude = Get-Content -Raw -LiteralPath (Join-Path $newProfilePath ".claude.json") | ConvertFrom-Json
    Assert-True ($newClaude.mcpServers.roslyn.command -eq "cmd") "Missing Claude configuration is created"
    $newCodex = [IO.File]::ReadAllText((Join-Path $newProfilePath ".codex\config.toml"))
    Assert-True ($newCodex.Contains("[mcp_servers.roslyn]")) "Missing Codex configuration is created"

    [void](New-Item -ItemType Directory -Path (Join-Path $plainProfilePath ".codex") -Force)
    [IO.File]::WriteAllText(
        (Join-Path $plainProfilePath ".claude.json"),
        '{ "theme": "light" }',
        [Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllText(
        (Join-Path $plainProfilePath ".codex\config.toml"),
        'model = "test"',
        [Text.UTF8Encoding]::new($false))
    Invoke-ConfigScript -ScriptPath $scriptPath -ProfilePath $plainProfilePath -SolutionPath $solutionPath
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
      "command": "cmd"
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
    Invoke-ConfigScript -ScriptPath $scriptPath -ProfilePath $duplicateKeyProfilePath -SolutionPath $solutionPath
    $duplicateResult = [IO.File]::ReadAllText($duplicateClaudePath)
    Assert-True ($duplicateResult.Contains('"projects"')) "Lower-case Claude root key is preserved"
    Assert-True ($duplicateResult.Contains('"Projects"')) "Case-distinct Claude root key is preserved"
    Assert-True (
        $duplicateResult.Contains('"ROSLYNMCP_BOOTSTRAP_SOLUTION_PATH"')
    ) "Claude MCP block is updated without parsing unrelated root keys"

    [void](New-Item -ItemType Directory -Path (Join-Path $invalidProfilePath ".codex") -Force)
    $invalidClaudePath = Join-Path $invalidProfilePath ".claude.json"
    $invalidCodexPath = Join-Path $invalidProfilePath ".codex\config.toml"
    [IO.File]::WriteAllText($invalidClaudePath, '{ invalid', [Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllText($invalidCodexPath, 'model = "untouched"', [Text.UTF8Encoding]::new($false))
    $failed = $false
    try {
        Invoke-ConfigScript -ScriptPath $scriptPath -ProfilePath $invalidProfilePath -SolutionPath $solutionPath
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
