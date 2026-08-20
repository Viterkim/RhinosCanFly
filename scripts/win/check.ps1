param()

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$projectRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$source = Join-Path $projectRoot "src"
$check = Join-Path $projectRoot "tools\check-all.fsx"

& dotnet fsi $check -- $source

if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}

Write-Host "Source checks passed."
