$ErrorActionPreference = "Stop"
$projectRoot = Split-Path -Parent $PSScriptRoot
$buildSetup = Join-Path $PSScriptRoot "build-setup.ps1"
$manifest = Join-Path $projectRoot "manifest.yml"
$dist = Join-Path $projectRoot "dist"

. $buildSetup -Quiet

if (-not (Test-Path -LiteralPath $YakPath)) {
    throw "Yak.exe was not found at '$YakPath'."
}

$versionMatch = Select-String -Path $manifest -Pattern '^\s*version:\s*([^\s#]+)' | Select-Object -First 1

if ($null -eq $versionMatch) {
    throw "Could not read the version from '$manifest'."
}

$version = $versionMatch.Matches[0].Groups[1].Value.Trim("'`"")
$packages = @()

foreach ($rhinoVersion in $ReleaseRhinoVersions) {
    $pattern = "rhinoscanfly-$version-rh$($rhinoVersion)_*-win.yak"
    $matches = @(Get-ChildItem -LiteralPath $dist -Filter $pattern -File -ErrorAction SilentlyContinue)

    if ($matches.Count -ne 1) {
        throw "Expected one Rhino $rhinoVersion Yak package matching '$pattern' in '$dist', found $($matches.Count). Run scripts\build-all-prod.ps1 first."
    }

    $packages += $matches[0]
}

foreach ($package in $packages) {
    Write-Host "Pushing to production: $($package.FullName)"
    & $YakPath push $package.FullName

    if ($LASTEXITCODE -ne 0) {
        exit $LASTEXITCODE
    }
}

$releaseNames = ($ReleaseRhinoVersions | ForEach-Object { "Rhino $_" }) -join " and "
Write-Host "Published $releaseNames packages for version $version."
