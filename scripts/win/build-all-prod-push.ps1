#Requires -Version 7.4

$ErrorActionPreference = "Stop"
$projectRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$buildSetup = Join-Path $PSScriptRoot "build-setup.ps1"
$dist = Join-Path $projectRoot "dist"
$version = & (Join-Path $PSScriptRoot 'set-versions.ps1') -Check

. $buildSetup -Quiet

if (-not (Test-Path -LiteralPath $YakPath)) {
    throw "Yak.exe was not found at '$YakPath'."
}

$packages = @()

foreach ($rhinoVersion in $ReleaseRhinoVersions) {
    $pattern = "rhinoscanfly-$version-rh$($rhinoVersion)_*-win.yak"
    $matches = @(Get-ChildItem -LiteralPath $dist -Filter $pattern -File -ErrorAction SilentlyContinue)

    if ($matches.Count -ne 1) {
        throw "Expected one Rhino $rhinoVersion Yak package matching '$pattern' in '$dist', found $($matches.Count). Run scripts\win\build-all-prod.ps1 first."
    }

    $packages += $matches[0]
}

& (Join-Path $projectRoot 'tools\manual\check-runtime-payload.ps1')
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

foreach ($package in $packages) {
    Write-Host "Pushing to production: $($package.FullName)"
    & $YakPath push $package.FullName

    if ($LASTEXITCODE -ne 0) {
        exit $LASTEXITCODE
    }
}

$releaseNames = ($ReleaseRhinoVersions | ForEach-Object { "Rhino $_" }) -join " and "
Write-Host "Published $releaseNames packages for version $version."
