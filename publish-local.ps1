# Build and publish to user-local directory
Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

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

try {
    & (Join-Path $PSScriptRoot "configure-clients.ps1")
}
catch {
    Write-Error "Server published, but client configuration failed: $($_.Exception.Message)"
    exit 1
}
