[CmdletBinding()]
param(
    [string]$BootstrapSolutionPath = (Join-Path $PSScriptRoot "RoslynMcp.sln"),
    [string]$UserProfilePath = $env:USERPROFILE,
    [switch]$SkipUserEnvironment
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$bootstrapVariable = "ROSLYNMCP_BOOTSTRAP_SOLUTION_PATH"
$bootstrapReference = '$' + '{ROSLYNMCP_BOOTSTRAP_SOLUTION_PATH}'

function Get-TextState {
    param([Parameter(Mandatory)][string]$Path)

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        return [pscustomobject]@{
            Exists = $false
            Hash = $null
            Text = $null
            Newline = [Environment]::NewLine
        }
    }

    $text = [IO.File]::ReadAllText($Path)
    $crlf = [string][char]13 + [char]10
    [pscustomobject]@{
        Exists = $true
        Hash = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash
        Text = $text
        Newline = if ($text.Contains($crlf)) { $crlf } else { [string][char]10 }
    }
}

function Write-TextAtomic {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$Text,
        [Parameter(Mandatory)]$ExpectedState
    )

    $currentExists = Test-Path -LiteralPath $Path -PathType Leaf
    if ($ExpectedState.Exists -ne $currentExists) {
        throw "Configuration changed while it was being prepared: $Path"
    }
    if ($currentExists) {
        $currentHash = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash
        if ($currentHash -ne $ExpectedState.Hash) {
            throw "Configuration changed while it was being prepared: $Path"
        }
    }

    $directory = Split-Path -Parent $Path
    [void](New-Item -ItemType Directory -Path $directory -Force)
    $temporaryPath = Join-Path $directory ".$([IO.Path]::GetFileName($Path)).$([guid]::NewGuid().ToString('N')).tmp"
    try {
        [IO.File]::WriteAllText($temporaryPath, $Text, [Text.UTF8Encoding]::new($false))
        Move-Item -LiteralPath $temporaryPath -Destination $Path -Force
    }
    finally {
        if (Test-Path -LiteralPath $temporaryPath) {
            Remove-Item -LiteralPath $temporaryPath -Force
        }
    }
}

function Find-MatchingJsonBrace {
    param(
        [Parameter(Mandatory)][string]$Text,
        [Parameter(Mandatory)][int]$Start
    )

    $depth = 0
    $inString = $false
    $escaped = $false
    for ($i = $Start; $i -lt $Text.Length; $i++) {
        $character = $Text[$i]
        if ($inString) {
            if ($escaped) {
                $escaped = $false
            }
            elseif ($character -eq '\') {
                $escaped = $true
            }
            elseif ($character -eq '"') {
                $inString = $false
            }
            continue
        }

        if ($character -eq '"') {
            $inString = $true
        }
        elseif ($character -eq '{') {
            $depth++
        }
        elseif ($character -eq '}') {
            $depth--
            if ($depth -eq 0) {
                return $i
            }
        }
    }

    throw "Could not find the end of the mcpServers object."
}

function New-ClaudeRoslynEntry {
    [pscustomobject][ordered]@{
        type = "stdio"
        command = "cmd"
        args = @("/c", '%LOCALAPPDATA%\RoslynMcp\RoslynMcp.Server.exe')
        env = [pscustomobject][ordered]@{
            ROSLYNMCP_BOOTSTRAP_SOLUTION_PATH = $bootstrapReference
        }
        timeout = 120000
    }
}

function Update-ClaudeConfig {
    param([Parameter(Mandatory)][string]$Path)

    $state = Get-TextState -Path $Path
    if (-not $state.Exists) {
        $root = [pscustomobject][ordered]@{
            mcpServers = [pscustomobject][ordered]@{
                roslyn = New-ClaudeRoslynEntry
            }
        }
        $content = ($root | ConvertTo-Json -Depth 20) + $state.Newline
        Write-TextAtomic -Path $Path -Text $content -ExpectedState $state
        Write-Host "INFO: Created Claude Code MCP configuration at $Path"
        return
    }

    try {
        $root = $state.Text | ConvertFrom-Json
    }
    catch {
        throw "Claude Code configuration is not valid JSON: $Path"
    }

    $property = $root.PSObject.Properties["mcpServers"]
    if ($null -eq $property) {
        $trimmed = $state.Text.TrimEnd()
        $closingBrace = $trimmed.LastIndexOf('}')
        if ($closingBrace -lt 0) {
            throw "Claude Code configuration has no root object: $Path"
        }
        $prefix = $trimmed.Substring(0, $closingBrace).TrimEnd()
        $separator = if ($root.PSObject.Properties.Count -eq 0) { "" } else { "," }
        $servers = [pscustomobject][ordered]@{ roslyn = New-ClaudeRoslynEntry }
        $rendered = $servers | ConvertTo-Json -Depth 20
        $rendered = $rendered.Replace([string][char]10, $state.Newline + "  ")
        $content = $prefix + $separator + $state.Newline + '  "mcpServers": ' + $rendered + $state.Newline + '}' + $state.Newline
        Write-TextAtomic -Path $Path -Text $content -ExpectedState $state
        Write-Host "INFO: Added the Roslyn MCP server to Claude Code configuration"
        return
    }

    $matches = [regex]::Matches($state.Text, '(?m)^  "mcpServers"\s*:\s*\{')
    if ($matches.Count -ne 1) {
        throw "Claude Code mcpServers must be a two-space-indented top-level object: $Path"
    }

    $start = $state.Text.IndexOf('{', $matches[0].Index)
    $end = Find-MatchingJsonBrace -Text $state.Text -Start $start
    $serversText = $state.Text.Substring($start, $end - $start + 1)
    $servers = $serversText | ConvertFrom-Json

    $roslynProperty = $servers.PSObject.Properties["roslyn"]
    if ($null -eq $roslynProperty) {
        $servers | Add-Member -NotePropertyName "roslyn" -NotePropertyValue (New-ClaudeRoslynEntry)
    }
    else {
        $roslyn = $roslynProperty.Value
        $envProperty = $roslyn.PSObject.Properties["env"]
        if ($null -eq $envProperty) {
            $roslyn | Add-Member -NotePropertyName "env" -NotePropertyValue ([pscustomobject][ordered]@{
                ROSLYNMCP_BOOTSTRAP_SOLUTION_PATH = $bootstrapReference
            })
        }
        else {
            $environment = $envProperty.Value
            $bootstrapProperty = $environment.PSObject.Properties[$bootstrapVariable]
            if ($null -eq $bootstrapProperty) {
                $environment | Add-Member -NotePropertyName $bootstrapVariable -NotePropertyValue $bootstrapReference
            }
            else {
                $bootstrapProperty.Value = $bootstrapReference
            }
        }
    }

    $rendered = $servers | ConvertTo-Json -Depth 20
    $rendered = $rendered.Replace([string][char]10, $state.Newline + "  ")
    $content = $state.Text.Substring(0, $start) + $rendered + $state.Text.Substring($end + 1)
    if ($content -eq $state.Text) {
        Write-Host "DEBUG: Claude Code MCP configuration is already current"
        return
    }

    Write-TextAtomic -Path $Path -Text $content -ExpectedState $state
    Write-Host "INFO: Configured Claude Code to forward $bootstrapVariable"
}

function New-CodexRoslynSection {
    param([Parameter(Mandatory)][string]$Newline)

    @(
        '[mcp_servers.roslyn]'
        'command = "cmd"'
        'args = ["/c", "%LOCALAPPDATA%\\RoslynMcp\\RoslynMcp.Server.exe"]'
        ('env_vars = ["{0}"]' -f $bootstrapVariable)
        'tool_timeout_sec = 60'
        'startup_timeout_sec = 120'
        ''
    ) -join $Newline
}

function Update-CodexConfig {
    param([Parameter(Mandatory)][string]$Path)

    $state = Get-TextState -Path $Path
    if (-not $state.Exists) {
        $content = New-CodexRoslynSection -Newline $state.Newline
        Write-TextAtomic -Path $Path -Text $content -ExpectedState $state
        Write-Host "INFO: Created Codex MCP configuration at $Path"
        return
    }

    $sectionMatches = [regex]::Matches(
        $state.Text,
        '(?ms)^\[mcp_servers\.roslyn\][^\r\n]*(?:\r?\n|$).*?(?=^\[|\z)')
    if ($sectionMatches.Count -gt 1) {
        throw "Codex configuration contains more than one roslyn MCP section: $Path"
    }
    if ($sectionMatches.Count -eq 0) {
        $separator = if ($state.Text.EndsWith($state.Newline)) { $state.Newline } else { $state.Newline + $state.Newline }
        $content = $state.Text + $separator + (New-CodexRoslynSection -Newline $state.Newline)
        Write-TextAtomic -Path $Path -Text $content -ExpectedState $state
        Write-Host "INFO: Added the Roslyn MCP server to Codex configuration"
        return
    }

    $sectionMatch = $sectionMatches[0]
    $section = $sectionMatch.Value
    $envMatches = [regex]::Matches(
        $section,
        '(?m)^(?<indent>[ \t]*)env_vars[ \t]*=[ \t]*\[(?<items>[^\]\r\n]*)\][ \t]*$')
    if ($envMatches.Count -gt 1) {
        throw "Codex roslyn MCP configuration contains more than one env_vars setting: $Path"
    }

    if ($envMatches.Count -eq 1) {
        $envMatch = $envMatches[0]
        $items = $envMatch.Groups["items"].Value
        if ($items.Contains('"' + $bootstrapVariable + '"') -or $items.Contains("'" + $bootstrapVariable + "'")) {
            Write-Host "DEBUG: Codex MCP configuration is already current"
            return
        }
        $items = $items.Trim()
        $updatedItems = if ($items) { $items + ', "' + $bootstrapVariable + '"' } else { '"' + $bootstrapVariable + '"' }
        $updatedEnv = $envMatch.Groups["indent"].Value + "env_vars = [$updatedItems]"
        $updatedSection = $section.Substring(0, $envMatch.Index) + $updatedEnv + $section.Substring($envMatch.Index + $envMatch.Length)
    }
    else {
        if ($section -match '(?m)^[ \t]*env_vars[ \t]*=') {
            throw "Codex roslyn MCP env_vars must be a single-line array: $Path"
        }
        $headerEnd = $section.IndexOf($state.Newline)
        if ($headerEnd -lt 0) {
            $updatedSection = $section + $state.Newline + ('env_vars = ["{0}"]' -f $bootstrapVariable) + $state.Newline
        }
        else {
            $insertAt = $headerEnd + $state.Newline.Length
            $updatedSection = $section.Insert($insertAt, ('env_vars = ["{0}"]{1}' -f $bootstrapVariable, $state.Newline))
        }
    }

    $content = $state.Text.Substring(0, $sectionMatch.Index) + $updatedSection + $state.Text.Substring($sectionMatch.Index + $sectionMatch.Length)
    Write-TextAtomic -Path $Path -Text $content -ExpectedState $state
    Write-Host "INFO: Configured Codex to forward $bootstrapVariable"
}

$defaultBootstrap = [IO.Path]::GetFullPath($BootstrapSolutionPath)
if (-not (Test-Path -LiteralPath $defaultBootstrap -PathType Leaf)) {
    throw "Bootstrap solution does not exist: $defaultBootstrap"
}
if ([string]::IsNullOrWhiteSpace($UserProfilePath)) {
    throw "A user profile path is required."
}

if (-not $SkipUserEnvironment) {
    $existingBootstrap = [Environment]::GetEnvironmentVariable($bootstrapVariable, "User")
    if (-not [string]::IsNullOrWhiteSpace($existingBootstrap) -and
        (Test-Path -LiteralPath $existingBootstrap -PathType Leaf)) {
        $effectiveBootstrap = [IO.Path]::GetFullPath($existingBootstrap)
        Write-Host "DEBUG: Preserving existing $bootstrapVariable=$effectiveBootstrap"
    }
    else {
        $effectiveBootstrap = $defaultBootstrap
        [Environment]::SetEnvironmentVariable($bootstrapVariable, $effectiveBootstrap, "User")
        Write-Host "INFO: Set user environment variable $bootstrapVariable=$effectiveBootstrap"
    }
    [Environment]::SetEnvironmentVariable($bootstrapVariable, $effectiveBootstrap, "Process")
}

Update-ClaudeConfig -Path (Join-Path $UserProfilePath ".claude.json")
Update-CodexConfig -Path (Join-Path $UserProfilePath ".codex\config.toml")

Write-Host "INFO: Client configuration complete. Restart Claude Code and Codex to use it."
