param(
    [int] $RhinoVersion = 0,
    [ValidateSet("None", "Test", "Production")]
    [string] $Publish = "None",
    [switch] $Clean
)

$ErrorActionPreference = "Stop"
$projectRoot = Split-Path -Parent $PSScriptRoot
$buildScript = Join-Path $projectRoot "build.ps1"
$settings = Join-Path $PSScriptRoot "build-settings.ps1"
$manifest = Join-Path $projectRoot "manifest.yml"
$dist = Join-Path $projectRoot "dist"

$buildParameters = @{
    Configuration = "Release"
    Clean = $Clean.IsPresent
}

if ($PSBoundParameters.ContainsKey("RhinoVersion")) {
    $buildParameters.RhinoVersion = $RhinoVersion
}

& $buildScript @buildParameters
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

. $settings

if (-not (Test-Path -LiteralPath $YakPath)) {
    throw "Yak.exe was not found at '$YakPath'."
}

$output = Join-Path $projectRoot "bin\Release\$TargetFramework"
$stage = Join-Path $dist "stage-rh$RhinoMajorVersion"

if (Test-Path -LiteralPath $stage) {
    Remove-Item -LiteralPath $stage -Recurse -Force
}

New-Item -ItemType Directory -Path $stage -Force | Out-Null

$packageFiles = @(
    (Join-Path $output "RhinosCanFly.rhp")
    $manifest
    (Join-Path $projectRoot "icon.png")
    (Join-Path $projectRoot "README.md")
    (Join-Path $projectRoot "LICENSE")
)

foreach ($file in $packageFiles) {
    if (-not (Test-Path -LiteralPath $file)) {
        throw "Package file was not found: '$file'."
    }

    Copy-Item -LiteralPath $file -Destination $stage
}

$dependencyFiles = @(
    Get-ChildItem -LiteralPath $output -Filter "*.dll" |
        Where-Object { $_.Name -notin @("RhinoCommon.dll", "Rhino.UI.dll", "Eto.dll") }
)

foreach ($file in $dependencyFiles) {
    Copy-Item -LiteralPath $file.FullName -Destination $stage
}

$versionMatch = Select-String -Path $manifest -Pattern '^\s*version:\s*([^\s#]+)' | Select-Object -First 1

if ($null -eq $versionMatch) {
    throw "Could not read the version from '$manifest'."
}

$version = $versionMatch.Matches[0].Groups[1].Value.Trim("'`"")
$zip = Join-Path $dist "RhinosCanFly-$version-rh$RhinoMajorVersion-win.zip"

if (Test-Path -LiteralPath $zip) {
    Remove-Item -LiteralPath $zip -Force
}

Compress-Archive -Path (Join-Path $stage "*") -DestinationPath $zip

Push-Location $stage

try {
    & $YakPath build --platform win
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}
finally {
    Pop-Location
}

$yakPackages = @(Get-ChildItem -LiteralPath $stage -Filter "*.yak")

if ($yakPackages.Count -ne 1) {
    throw "Expected one Yak package in '$stage', found $($yakPackages.Count)."
}

$yakPackage = Join-Path $dist $yakPackages[0].Name
Copy-Item -LiteralPath $yakPackages[0].FullName -Destination $yakPackage -Force

if ($Publish -eq "Test") {
    & $YakPath push --source "https://test.yak.rhino3d.com" $yakPackage
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}
elseif ($Publish -eq "Production") {
    & $YakPath push $yakPackage
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

Write-Host "Manual ZIP: $zip"
Write-Host "Yak package: $yakPackage"
