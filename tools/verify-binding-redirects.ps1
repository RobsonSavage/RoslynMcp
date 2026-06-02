# verify-binding-redirects.ps1
# Verifies that Visual Studio devenv.exe.config contains the required binding
# redirects for RoslynMcp extension assemblies, and (in Full mode) validates
# version ranges and checks for leaked DLLs in build output.

param(
    [ValidateSet('CIPreflight', 'Full')]
    [string]$Mode = 'CIPreflight'
)

$ErrorActionPreference = 'Stop'

$script:HasErrors = $false

function Write-Pass([string]$Message) {
    Write-Host "  [PASS] $Message" -ForegroundColor Green
}

function Write-Fail([string]$Message) {
    Write-Host "  [FAIL] $Message" -ForegroundColor Red
    $script:HasErrors = $true
}

function Write-Section([string]$Message) {
    Write-Host "`n== $Message ==" -ForegroundColor Cyan
}

# ---------------------------------------------------------------------------
# Locate Visual Studio
# ---------------------------------------------------------------------------
Write-Section "Locating Visual Studio"

$vswhereExe = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
if (-not (Test-Path $vswhereExe)) {
    Write-Fail "vswhere.exe not found at: $vswhereExe"
    exit 1
}
Write-Pass "vswhere.exe found"

$vsPath = & $vswhereExe -latest -property installationPath
if ([string]::IsNullOrWhiteSpace($vsPath)) {
    Write-Fail "vswhere returned empty installation path"
    exit 1
}
Write-Pass "VS installation: $vsPath"

# ---------------------------------------------------------------------------
# Locate and parse devenv.exe.config
# ---------------------------------------------------------------------------
Write-Section "Checking devenv.exe.config"

$configPath = Join-Path $vsPath 'Common7\IDE\devenv.exe.config'
if (-not (Test-Path $configPath)) {
    Write-Fail "devenv.exe.config not found at: $configPath"
    exit 1
}
Write-Pass "Config file exists"

[xml]$configXml = Get-Content -Path $configPath -Raw

# ---------------------------------------------------------------------------
# Required binding redirect assemblies
# ---------------------------------------------------------------------------
$requiredAssemblies = @(
    'Microsoft.CodeAnalysis'
    'Microsoft.CodeAnalysis.CSharp'
    'Microsoft.CodeAnalysis.Workspaces'
    'Microsoft.CodeAnalysis.CSharp.Workspaces'
    'Microsoft.VisualStudio.LanguageServices'
)

Write-Section "Binding Redirect Verification"

# Namespace manager for the assemblyBinding elements
$nsManager = New-Object System.Xml.XmlNamespaceManager($configXml.NameTable)
$nsManager.AddNamespace('bind', 'urn:schemas-microsoft-com:asm.v1')

$redirectNodes = $configXml.SelectNodes(
    '//bind:assemblyBinding/bind:dependentAssembly', $nsManager
)

# Build a lookup of assembly name -> dependentAssembly node
$redirectMap = @{}
foreach ($node in $redirectNodes) {
    $identity = $node.SelectSingleNode('bind:assemblyIdentity', $nsManager)
    if ($identity) {
        $name = $identity.GetAttribute('name')
        if ($name) {
            $redirectMap[$name] = $node
        }
    }
}

foreach ($assembly in $requiredAssemblies) {
    if ($redirectMap.ContainsKey($assembly)) {
        Write-Pass "Binding redirect found: $assembly"
    } else {
        Write-Fail "Binding redirect MISSING: $assembly"
    }
}

if ($script:HasErrors) {
    Write-Host "`nCIPreflight FAILED." -ForegroundColor Red
    exit 1
}

# ---------------------------------------------------------------------------
# Full mode: additional checks
# ---------------------------------------------------------------------------
if ($Mode -eq 'Full') {

    # -- Version range validation ------------------------------------------
    Write-Section "Version Range Validation"

    foreach ($assembly in $requiredAssemblies) {
        $node = $redirectMap[$assembly]
        $redirect = $node.SelectSingleNode('bind:bindingRedirect', $nsManager)
        if (-not $redirect) {
            Write-Fail "$assembly : no <bindingRedirect> element found"
            continue
        }

        $oldVersion = $redirect.GetAttribute('oldVersion')
        $newVersion = $redirect.GetAttribute('newVersion')
        $expectedOldVersion = "0.0.0.0-$newVersion"

        if ($oldVersion -eq $expectedOldVersion) {
            Write-Pass "$assembly : oldVersion=$oldVersion covers full range to newVersion=$newVersion"
        } else {
            Write-Fail "$assembly : oldVersion='$oldVersion' expected='$expectedOldVersion' (newVersion=$newVersion)"
        }
    }

    # -- Leaked DLL checks -------------------------------------------------
    Write-Section "Leaked DLL Checks"

    $repoRoot = Split-Path $PSScriptRoot -Parent   # tools/ -> RoslynMcp root

    $releaseDirs = @(
        Join-Path $repoRoot 'src\RoslynMcp.Extension\bin\Release'
        Join-Path $repoRoot 'src\RoslynMcp.Server\bin\Release'
    )

    foreach ($dir in $releaseDirs) {
        $shortDir = $dir.Replace($repoRoot, '.')

        if (-not (Test-Path $dir)) {
            Write-Fail "Build output directory missing: $shortDir  (run a Release build first)"
            continue
        }

        $leakedDlls = Get-ChildItem -Path $dir -Filter 'Microsoft.CodeAnalysis*.dll' -Recurse -File
        if ($leakedDlls.Count -gt 0) {
            Write-Fail "Leaked DLLs found in ${shortDir}:"
            foreach ($dll in $leakedDlls) {
                Write-Host "         $($dll.FullName)" -ForegroundColor Yellow
            }
        } else {
            Write-Pass "No leaked Microsoft.CodeAnalysis*.dll in $shortDir"
        }
    }

    if ($script:HasErrors) {
        Write-Host "`nFull verification FAILED." -ForegroundColor Red
        exit 1
    }
}

# ---------------------------------------------------------------------------
Write-Host "`n$Mode verification PASSED." -ForegroundColor Green
exit 0
