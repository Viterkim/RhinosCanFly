param(
    [int] $RhinoVersion = 9,
    [switch] $SkipBuild,
    [switch] $Payload
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent

Push-Location $repo
try {
    & (Join-Path $PSScriptRoot 'format.ps1') -Check
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    & (Join-Path $PSScriptRoot 'check.ps1')
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    if (-not $SkipBuild) {
        & (Join-Path $PSScriptRoot 'build.ps1') -RhinoVersion $RhinoVersion -Configuration Release -SkipChecks -Quiet
        if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
        Write-Host "Rhino $RhinoVersion build passed."
    }

    if ($Payload) {
        & (Join-Path $repo 'tools\manual\check-runtime-payload.ps1') -RhinoVersion $RhinoVersion
        if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    }

    exit 0
}
finally {
    Pop-Location
}
