[CmdletBinding()]
param([switch] $CheckOnly)

$ErrorActionPreference = "Stop"
$projectRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$dist = Join-Path $projectRoot "dist"
$version = & (Join-Path $PSScriptRoot 'set-versions.ps1') -Check
$packages = @()

Push-Location $projectRoot

try {
    foreach ($rhinoVersion in @(8, 9)) {
        $output = & dotnet msbuild (Join-Path $projectRoot 'RhinosCanFly.fsproj') -nologo `
            -p:RhinosCanFlyPlatform=mac "-p:RhinoMajorVersion=$rhinoVersion" `
            -getProperty:TargetFramework -getProperty:RhinoCommonPackageVersion

        if ($LASTEXITCODE -ne 0) {
            throw "Could not read the Rhino $rhinoVersion Mac build profile."
        }

        $profile = (($output -join [Environment]::NewLine) | ConvertFrom-Json).Properties
        $baseline = ($profile.RhinoCommonPackageVersion -split '[-.]')[0..1] -join '_'
        $yak = Join-Path $dist "rhinoscanfly-$version-rh$baseline-mac.yak"
        $zip = Join-Path $dist "RhinosCanFly-$version-rh$rhinoVersion-mac.zip"

        foreach ($path in @($zip, $yak)) {
            if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
                throw "Missing '$path'. Download and extract the Mac build into dist first."
            }
        }

        & dotnet fsi (Join-Path $projectRoot 'tools/check-mac-package.fsx') -- release `
            $projectRoot $version $profile.TargetFramework $profile.RhinoCommonPackageVersion $zip $yak

        if ($LASTEXITCODE -ne 0) {
            throw "Rhino $rhinoVersion Mac package validation failed. Nothing has been uploaded."
        }

        $packages += $yak
    }

    if ($CheckOnly) {
        Write-Host "Mac packages for version $version passed. Nothing uploaded."
        return
    }

    . (Join-Path $PSScriptRoot 'build-setup.ps1') -Quiet

    if (-not (Test-Path -LiteralPath $YakPath -PathType Leaf)) {
        throw "Yak.exe was not found. Install Rhino before publishing."
    }

    foreach ($package in $packages) {
        Write-Host "Pushing to production: $package"
        & $YakPath push $package

        if ($LASTEXITCODE -ne 0) {
            throw "Yak could not publish '$package'."
        }
    }

    Write-Host "Published Rhino 8 and 9 Mac packages for version $version."
}
finally {
    Pop-Location
}
