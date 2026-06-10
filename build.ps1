<#
.SYNOPSIS
    Builds and packages the ETLReader MCP server into a .nupkg. 
#>
param(
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"

$repoRoot   = $PSScriptRoot
$projectDir = Join-Path $repoRoot "src"
$outputDir  = Join-Path $repoRoot "package"

# Clean previous output
if (Test-Path $outputDir) {
    Remove-Item $outputDir -Recurse -Force
}
New-Item -ItemType Directory -Path $outputDir | Out-Null

Write-Host "Building and packing ETLReader ($Configuration)..." -ForegroundColor Cyan
dotnet pack $projectDir -c $Configuration -o $outputDir

if ($LASTEXITCODE -ne 0) {
    Write-Error "dotnet pack failed with exit code $LASTEXITCODE"
    exit $LASTEXITCODE
}

Write-Host "`nPackage created in: $outputDir" -ForegroundColor Green
Get-ChildItem $outputDir -Filter *.nupkg | ForEach-Object { Write-Host "  $_" }
