[CmdletBinding()]
param(
    [string]$UserProfilePath = $env:USERPROFILE,
    # The server is launched directly, so this must be a real path and not %LOCALAPPDATA%\...
    # Neither Claude Code nor Codex runs the command through a shell, so an environment token would
    # be handed to CreateProcess literally and the server would never start.
    [string]$ServerPath = (Join-Path $env:LOCALAPPDATA "RoslynMcp\RoslynMcp.Server.exe"),
    [switch]$SkipUserEnvironment
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$obsoleteBootstrapVariable = "ROSLYNMCP_BOOTSTRAP_SOLUTION_PATH"

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

function Assert-TextStateCurrent {
    param(
        [Parameter(Mandatory)][string]$Path,
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
}

function Write-TextAtomic {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$Text,
        [Parameter(Mandatory)]$ExpectedState
    )

    Assert-TextStateCurrent -Path $Path -ExpectedState $ExpectedState

    $directory = Split-Path -Parent $Path
    [void](New-Item -ItemType Directory -Path $directory -Force)
    $temporaryPath = Join-Path $directory ".$([IO.Path]::GetFileName($Path)).$([guid]::NewGuid().ToString('N')).tmp"
    try {
        [IO.File]::WriteAllText($temporaryPath, $Text, [Text.UTF8Encoding]::new($false))
        Assert-TextStateCurrent -Path $Path -ExpectedState $ExpectedState
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

# Launched directly rather than through `cmd /c`. cmd re-splits its command line on spaces AFTER
# expanding a variable, so `cmd /c %LOCALAPPDATA%\...` breaks outright for any user whose profile
# directory contains a space, and no quoting inside the args array survives the trip - the value is
# quoted again on its way to the process. Naming the executable as the command hands it to
# CreateProcess as a single argument, whatever it contains.
function New-ClaudeRoslynEntry {
    [pscustomobject][ordered]@{
        type = "stdio"
        command = $ServerPath
        args = [object[]]@()
        timeout = 120000
    }
}

function Set-ObjectMember {
    param(
        [Parameter(Mandatory)]$Target,
        [Parameter(Mandatory)][string]$Name,
        $Value
    )

    if ($null -ne $Target.PSObject.Properties[$Name]) {
        $Target.PSObject.Properties[$Name].Value = $Value
    }
    else {
        $Target | Add-Member -NotePropertyName $Name -NotePropertyValue $Value
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

    $matches = [regex]::Matches($state.Text, '(?m)^  "mcpServers"\s*:\s*\{')
    if ($matches.Count -gt 1) {
        throw "Claude Code configuration contains more than one top-level mcpServers object: $Path"
    }
    if ($matches.Count -eq 0) {
        try {
            $root = $state.Text | ConvertFrom-Json
        }
        catch {
            throw "Claude Code configuration is not valid JSON: $Path"
        }
        if ($null -ne $root.PSObject.Properties["mcpServers"]) {
            throw "Claude Code mcpServers must be a two-space-indented top-level object: $Path"
        }

        $trimmed = $state.Text.TrimEnd()
        $closingBrace = $trimmed.LastIndexOf('}')
        if ($closingBrace -lt 0) {
            throw "Claude Code configuration has no root object: $Path"
        }
        $prefix = $trimmed.Substring(0, $closingBrace).TrimEnd()
        $separator = if (@($root.PSObject.Properties).Count -eq 0) { "" } else { "," }
        $servers = [pscustomobject][ordered]@{ roslyn = New-ClaudeRoslynEntry }
        $rendered = $servers | ConvertTo-Json -Depth 20
        $rendered = $rendered.Replace(([string][char]13 + [char]10), [string][char]10)
        $rendered = $rendered.Replace([string][char]10, $state.Newline + "  ")
        $content = $prefix + $separator + $state.Newline + '  "mcpServers": ' + $rendered + $state.Newline + '}' + $state.Newline
        Write-TextAtomic -Path $Path -Text $content -ExpectedState $state
        Write-Host "INFO: Added the Roslyn MCP server to Claude Code configuration"
        return
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
        # Migrate an entry written by an older version of this script, which routed the executable
        # through cmd. Only the launcher is touched; timeout, env and anything else stay as they are.
        Set-ObjectMember -Target $roslyn -Name "command" -Value $ServerPath
        Set-ObjectMember -Target $roslyn -Name "args" -Value ([object[]]@())
        $envProperty = $roslyn.PSObject.Properties["env"]
        if ($null -ne $envProperty -and $null -ne $envProperty.Value) {
            $environment = $envProperty.Value
            if ($null -ne $environment.PSObject.Properties[$obsoleteBootstrapVariable]) {
                $environment.PSObject.Properties.Remove($obsoleteBootstrapVariable)
            }
            if (@($environment.PSObject.Properties).Count -eq 0) {
                $roslyn.PSObject.Properties.Remove("env")
            }
        }
        elseif ($null -ne $envProperty) {
            $roslyn.PSObject.Properties.Remove("env")
        }
    }

    $rendered = $servers | ConvertTo-Json -Depth 20
    $rendered = $rendered.Replace(([string][char]13 + [char]10), [string][char]10)
    $rendered = $rendered.Replace([string][char]10, $state.Newline + "  ")
    $content = $state.Text.Substring(0, $start) + $rendered + $state.Text.Substring($end + 1)
    if ($content -eq $state.Text) {
        Write-Host "DEBUG: Claude Code MCP configuration is already current"
        return
    }

    Write-TextAtomic -Path $Path -Text $content -ExpectedState $state
    Write-Host "INFO: Updated the Claude Code Roslyn MCP entry"
}

function ConvertTo-TomlString {
    param([Parameter(Mandatory)][string]$Value)

    '"' + $Value.Replace('\', '\\').Replace('"', '\"') + '"'
}

# Set one single-line key inside an already-isolated section, appending it after the section header
# when absent. A key whose value spans lines is refused rather than half-rewritten: the single-line
# pattern would match only its first line and leave the remainder behind as stray TOML.
function Set-TomlSetting {
    param(
        [Parameter(Mandatory)][string]$Section,
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][string]$Literal,
        [Parameter(Mandatory)][string]$Newline,
        [Parameter(Mandatory)][string]$Path
    )

    $escapedName = [regex]::Escape($Name)
    $present = [regex]::Matches($Section, '(?m)^[ \t]*' + $escapedName + '[ \t]*=')
    if ($present.Count -gt 1) {
        throw "Codex roslyn MCP configuration contains more than one $Name setting: $Path"
    }

    $single = [regex]::Matches(
        $Section,
        '(?m)^(?<indent>[ \t]*)' + $escapedName + '[ \t]*=[ \t]*(?:\[[^\]\r\n]*\]|"(?:[^"\\\r\n]|\\.)*"|[^\r\n]*)[ \t]*$')
    if ($present.Count -ne $single.Count) {
        throw "Codex roslyn MCP $Name must be a single-line value: $Path"
    }

    if ($single.Count -eq 1) {
        $match = $single[0]
        $replacement = $match.Groups["indent"].Value + "$Name = $Literal"
        return $Section.Substring(0, $match.Index) + $replacement + $Section.Substring($match.Index + $match.Length)
    }

    $header = [regex]::Match($Section, '(?m)^\[mcp_servers\.roslyn\][^\r\n]*(?:\r?\n|$)')
    if (-not $header.Success) {
        throw "Codex roslyn MCP section has no header: $Path"
    }
    $insertAt = $header.Index + $header.Length
    return $Section.Substring(0, $insertAt) + "$Name = $Literal" + $Newline + $Section.Substring($insertAt)
}

# Same reasoning as New-ClaudeRoslynEntry: Codex spawns the command directly, so it gets the real
# path rather than cmd plus an environment token.
function New-CodexRoslynSection {
    param([Parameter(Mandatory)][string]$Newline)

    @(
        '[mcp_servers.roslyn]'
        'command = ' + (ConvertTo-TomlString -Value $ServerPath)
        'args = []'
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
    $updatedSection = $sectionMatch.Value
    $envMatches = [regex]::Matches(
        $updatedSection,
        '(?m)^(?<indent>[ \t]*)env_vars[ \t]*=[ \t]*\[(?<items>[^\]\r\n]*)\][ \t]*$')
    if ($envMatches.Count -gt 1) {
        throw "Codex roslyn MCP configuration contains more than one env_vars setting: $Path"
    }

    if ($envMatches.Count -eq 1) {
        $envMatch = $envMatches[0]
        $items = $envMatch.Groups["items"].Value
        if ($items.Contains('"' + $obsoleteBootstrapVariable + '"') -or
            $items.Contains("'" + $obsoleteBootstrapVariable + "'")) {
            $valueMatches = [regex]::Matches($items, '(?<quote>["''])(?<value>[^"'']+)\k<quote>')
            if ($valueMatches.Count -eq 0 -and -not [string]::IsNullOrWhiteSpace($items)) {
                throw "Codex roslyn MCP env_vars contains an unsupported value: $Path"
            }
            $remaining = @($valueMatches | ForEach-Object { $_.Groups["value"].Value } |
                Where-Object { $_ -ne $obsoleteBootstrapVariable })
            if ($remaining.Count -eq 0) {
                $removeLength = $envMatch.Length
                if ($envMatch.Index + $removeLength -lt $updatedSection.Length -and
                    $updatedSection.Substring($envMatch.Index + $removeLength).StartsWith($state.Newline)) {
                    $removeLength += $state.Newline.Length
                }
                $updatedSection = $updatedSection.Remove($envMatch.Index, $removeLength)
            }
            else {
                $updatedItems = ($remaining | ForEach-Object { '"' + $_ + '"' }) -join ', '
                $updatedEnv = $envMatch.Groups["indent"].Value + "env_vars = [$updatedItems]"
                $updatedSection = $updatedSection.Substring(0, $envMatch.Index) + $updatedEnv + $updatedSection.Substring($envMatch.Index + $envMatch.Length)
            }
        }
    }
    elseif ($updatedSection -match '(?m)^[ \t]*env_vars[ \t]*=') {
        throw "Codex roslyn MCP env_vars must be a single-line array: $Path"
    }

    # Migrate a section written by an older version of this script, which routed the executable
    # through cmd. Runs unconditionally so the launcher is correct even when env_vars was clean.
    $updatedSection = Set-TomlSetting -Section $updatedSection -Name "command" `
        -Literal (ConvertTo-TomlString -Value $ServerPath) -Newline $state.Newline -Path $Path
    $updatedSection = Set-TomlSetting -Section $updatedSection -Name "args" `
        -Literal "[]" -Newline $state.Newline -Path $Path

    $content = $state.Text.Substring(0, $sectionMatch.Index) + $updatedSection + $state.Text.Substring($sectionMatch.Index + $sectionMatch.Length)
    if ($content -eq $state.Text) {
        Write-Host "DEBUG: Codex MCP configuration is already current"
        return
    }

    Write-TextAtomic -Path $Path -Text $content -ExpectedState $state
    Write-Host "INFO: Updated the Codex Roslyn MCP section"
}

if ([string]::IsNullOrWhiteSpace($UserProfilePath)) {
    throw "A user profile path is required."
}

if (-not $SkipUserEnvironment) {
    $nullString = [System.Management.Automation.Language.NullString]::Value
    [Environment]::SetEnvironmentVariable($obsoleteBootstrapVariable, $nullString, "User")
    [Environment]::SetEnvironmentVariable($obsoleteBootstrapVariable, $nullString, "Process")
    Write-Host "INFO: Removed obsolete user environment variable $obsoleteBootstrapVariable"
}

Update-ClaudeConfig -Path (Join-Path $UserProfilePath ".claude.json")
Update-CodexConfig -Path (Join-Path $UserProfilePath ".codex\config.toml")

Write-Host "INFO: Client configuration complete. Restart Claude Code and Codex to use it."
