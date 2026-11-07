param([switch] $CheckOnly)

$ErrorActionPreference = "Stop"
$projectRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$buildSetup = Join-Path $PSScriptRoot "build-setup.ps1"
$dist = Join-Path $projectRoot "dist"
$version = & (Join-Path $PSScriptRoot 'set-versions.ps1') -Check
$macPublisher = Join-Path $PSScriptRoot 'publish-mac.ps1'

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

$macPackages = @(
    Get-ChildItem -LiteralPath $dist -File | Where-Object {
        $_.Name -like "rhinoscanfly-$version-rh*-mac.yak" -or
        $_.Name -like "RhinosCanFly-$version-rh*-mac.zip"
    }
)

if ($macPackages.Count -gt 0) {
    & $macPublisher -CheckOnly
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}
else {
    Write-Host "No Mac packages for version $version in dist. Using Windows packages."
}

if ($CheckOnly) {
    Write-Host "Release packages for version $version passed. Nothing uploaded."
    return
}

foreach ($package in $packages) {
    Write-Host "Pushing to production: $($package.FullName)"
    & $YakPath push $package.FullName

    if ($LASTEXITCODE -ne 0) {
        exit $LASTEXITCODE
    }
}

if ($macPackages.Count -gt 0) {
    & $macPublisher
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

$platforms = if ($macPackages.Count -gt 0) { "Windows and Mac" } else { "Windows" }
Write-Host "Published $platforms packages for version $version."
