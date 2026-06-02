# verify-build.ps1
# Build verification for RoslynMcp: build, test, and DLL presence/leak checks.
# Exit 0 = all checks passed, Exit 1 = one or more failures.

param(
    [ValidateSet('Release', 'Debug')]
    [string]$Configuration = 'Release',
    [switch]$SkipBuild,
    [switch]$SkipTests,
    [switch]$SkipExtensionCheck,
    [switch]$SkipServerCheck
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
# Locate solution
# ---------------------------------------------------------------------------
Write-Section "Locating Solution"

$repoRoot = Split-Path $PSScriptRoot -Parent   # tools/ -> RoslynMcp root
$slnPath  = Join-Path $repoRoot 'RoslynMcp.sln'

if (-not (Test-Path $slnPath)) {
    Write-Fail "RoslynMcp.sln not found at: $slnPath"
    exit 1
}
Write-Pass "Solution found: $slnPath"

# ---------------------------------------------------------------------------
# Build
# ---------------------------------------------------------------------------
if (-not $SkipBuild) {
    Write-Section "Build ($Configuration)"

    Push-Location $repoRoot
    try {
        & dotnet restore RoslynMcp.sln
        if ($LASTEXITCODE -ne 0) {
            Write-Fail "dotnet restore failed (exit code $LASTEXITCODE)"
        } else {
            Write-Pass "dotnet restore succeeded"
        }

        & dotnet build RoslynMcp.sln -c $Configuration --no-restore
        if ($LASTEXITCODE -ne 0) {
            Write-Fail "dotnet build failed (exit code $LASTEXITCODE)"
        } else {
            Write-Pass "dotnet build succeeded"
        }
    } finally {
        Pop-Location
    }

    if ($script:HasErrors) {
        Write-Host "`nBuild FAILED — skipping remaining checks." -ForegroundColor Red
        exit 1
    }
}

# ---------------------------------------------------------------------------
# Tests
# ---------------------------------------------------------------------------
if (-not $SkipTests) {
    Write-Section "Tests ($Configuration)"

    Push-Location $repoRoot
    try {
        & dotnet test RoslynMcp.sln -c $Configuration --no-build
        if ($LASTEXITCODE -ne 0) {
            Write-Fail "dotnet test failed (exit code $LASTEXITCODE)"
        } else {
            Write-Pass "All tests passed"
        }
    } finally {
        Pop-Location
    }
}

# ---------------------------------------------------------------------------
# Extension DLL leak check
# ---------------------------------------------------------------------------
if (-not $SkipExtensionCheck) {
    Write-Section "Extension DLL Leak Check"

    $extensionBin = Join-Path $repoRoot "src\RoslynMcp.Extension\bin\$Configuration"

    if (-not (Test-Path $extensionBin)) {
        Write-Fail "Extension bin directory missing: $extensionBin (build first)"
    } else {
        $leakedDlls = Get-ChildItem -Path $extensionBin -Filter 'Microsoft.CodeAnalysis*.dll' -Recurse -File
        if ($leakedDlls.Count -gt 0) {
            Write-Fail "Roslyn DLLs leaked into Extension output ($($leakedDlls.Count) found):"
            foreach ($dll in $leakedDlls) {
                Write-Host "         $($dll.FullName.Replace($repoRoot, '.'))" -ForegroundColor Yellow
            }
        } else {
            Write-Pass "No Microsoft.CodeAnalysis*.dll in Extension output"
        }
    }
}

# ---------------------------------------------------------------------------
# Server DLL presence check
# ---------------------------------------------------------------------------
if (-not $SkipServerCheck) {
    Write-Section "Server DLL Presence Check"

    $serverBin = Join-Path $repoRoot "src\RoslynMcp.Server\bin\$Configuration"

    $requiredDlls = @(
        'Microsoft.CodeAnalysis.dll'
        'Microsoft.CodeAnalysis.CSharp.dll'
        'Microsoft.CodeAnalysis.Workspaces.dll'
        'Microsoft.CodeAnalysis.CSharp.Workspaces.dll'
        'Microsoft.CodeAnalysis.Workspaces.MSBuild.dll'
    )

    if (-not (Test-Path $serverBin)) {
        Write-Fail "Server bin directory missing: $serverBin (build first)"
    } else {
        foreach ($dll in $requiredDlls) {
            $found = Get-ChildItem -Path $serverBin -Filter $dll -Recurse -File
            if ($found.Count -gt 0) {
                Write-Pass "Server contains $dll"
            } else {
                Write-Fail "Server MISSING $dll"
            }
        }
    }
}

# ---------------------------------------------------------------------------
# Summary
# ---------------------------------------------------------------------------
Write-Host ""
if ($script:HasErrors) {
    Write-Host "Verification FAILED." -ForegroundColor Red
    exit 1
} else {
    Write-Host "Verification PASSED." -ForegroundColor Green
    exit 0
}
