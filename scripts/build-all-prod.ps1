$ErrorActionPreference = "Stop"
$yakScript = Join-Path $PSScriptRoot "yak.ps1"
$runningRhino = @(Get-Process -Name "Rhino" -ErrorAction SilentlyContinue)

if ($runningRhino.Count -gt 0) {
    throw "Rhino is running. Save your work, close Rhino yourself, then run scripts\build-all-prod.ps1 again."
}

foreach ($rhinoVersion in @(7, 8)) {
    Write-Host "Building clean Rhino $rhinoVersion release packages."
    & $yakScript -RhinoVersion $rhinoVersion -Clean -Publish None

    if ($LASTEXITCODE -ne 0) {
        exit $LASTEXITCODE
    }
}

Write-Host "Finished Rhino 7 and Rhino 8 release packages."
