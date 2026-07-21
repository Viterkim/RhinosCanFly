$ErrorActionPreference = "Stop"
$yakScript = Join-Path $PSScriptRoot "yak.ps1"
$buildSetup = Join-Path $PSScriptRoot "build-setup.ps1"
$runningRhino = @(Get-Process -Name "Rhino" -ErrorAction SilentlyContinue)

. $buildSetup -Quiet

if ($runningRhino.Count -gt 0) {
    throw "Rhino is running. Save your work, close Rhino yourself, then run scripts\build-all-prod.ps1 again."
}

foreach ($rhinoVersion in $ReleaseRhinoVersions) {
    Write-Host "Building clean Rhino $rhinoVersion release packages."
    & $yakScript -RhinoVersion $rhinoVersion -Clean -Publish None

    if ($LASTEXITCODE -ne 0) {
        exit $LASTEXITCODE
    }
}

$releaseNames = ($ReleaseRhinoVersions | ForEach-Object { "Rhino $_" }) -join " and "
Write-Host "Finished $releaseNames release packages."
