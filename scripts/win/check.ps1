param()

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$projectRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$source = Join-Path $projectRoot "src"
$tools = Join-Path $projectRoot "tools"
$check = Join-Path $projectRoot "tools\check-all.fsx"
$styleCheck = Join-Path $projectRoot "tools\check-source-style.fsx"

& dotnet fsi $check -- $source

if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}

& dotnet fsi $styleCheck -- $tools

if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}

Write-Host "Source checks passed."
