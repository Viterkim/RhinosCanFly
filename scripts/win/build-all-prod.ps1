$ErrorActionPreference = "Stop"
$yakScript = Join-Path $PSScriptRoot "yak.ps1"
$buildSetup = Join-Path $PSScriptRoot "build-setup.ps1"
$formatScript = Join-Path $PSScriptRoot "format.ps1"
$checkScript = Join-Path $PSScriptRoot "check.ps1"

. $buildSetup -Quiet -MatrixOnly

$runningRhinoVersions =
    @(
        foreach ($rhinoVersion in $ReleaseRhinoVersions) {
            $installation = Find-RhinoInstallation -Major $rhinoVersion

            if ($null -ne $installation) {
                $executable = Join-Path $installation.Install "System\Rhino.exe"

                if (@(Get-RunningRhinoProcess -ExecutablePath $executable).Count -gt 0) {
                    $rhinoVersion
                }
            }
        }
    )

if ($runningRhinoVersions.Count -gt 0) {
    $versions = $runningRhinoVersions -join ", "
    throw "Rhino $versions is running and is included in this release build. Save your work, close that Rhino version, then run scripts\win\build-all-prod.ps1 again."
}

& $formatScript -Check
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

& $checkScript
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

foreach ($rhinoVersion in $ReleaseRhinoVersions) {
    Write-Host "Building clean Rhino $rhinoVersion release packages."
    & $yakScript -RhinoVersion $rhinoVersion -Clean -SkipChecks -Publish None

    if ($LASTEXITCODE -ne 0) {
        exit $LASTEXITCODE
    }
}

$releaseNames = ($ReleaseRhinoVersions | ForEach-Object { "Rhino $_" }) -join " and "
Write-Host "Finished $releaseNames release packages."
