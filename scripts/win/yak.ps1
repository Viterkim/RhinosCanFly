param(
    [int] $RhinoVersion = 0,
    [ValidateSet("None", "Test", "Production")]
    [string] $Publish = "None",
    [switch] $Clean,
    [switch] $SkipChecks
)

$ErrorActionPreference = "Stop"
$projectRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$formatScript = Join-Path $PSScriptRoot "format.ps1"
$checkScript = Join-Path $PSScriptRoot "check.ps1"
$buildScript = Join-Path $PSScriptRoot "build.ps1"
$buildSetup = Join-Path $PSScriptRoot "build-setup.ps1"
$manifest = Join-Path $projectRoot "manifest.yml"
$dist = Join-Path $projectRoot "dist"
$version = & (Join-Path $PSScriptRoot 'set-versions.ps1') -Check

$setupParameters = @{ Quiet = $true }

if ($PSBoundParameters.ContainsKey("RhinoVersion")) {
    $setupParameters.RhinoVersion = $RhinoVersion
}

. $buildSetup @setupParameters

$buildParameters = @{
    Configuration = "Release"
    Clean = $Clean.IsPresent
    RhinoVersion = [int] $RhinoMajorVersion
    SkipChecks = $true
    SkipSetup = $true
    Quiet = $true
}

if (-not $SkipChecks) {
    & $formatScript -Check
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

    & $checkScript
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

& $buildScript @buildParameters
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

if (-not (Test-Path -LiteralPath $YakPath)) {
    throw "Yak.exe was not found at '$YakPath'."
}

$output = Join-Path $projectRoot "bin\rh$RhinoMajorVersion\Release\$TargetFramework"
$stage = [IO.Path]::GetFullPath((Join-Path $dist "stage-rh$RhinoMajorVersion"))
$stagePrefix = [IO.Path]::GetFullPath($dist).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar

if (-not $stage.StartsWith($stagePrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing unexpected package stage '$stage'."
}

if (Test-Path -LiteralPath $stage) {
    Remove-Item -LiteralPath $stage -Recurse -Force
}

New-Item -ItemType Directory -Path $stage -Force | Out-Null

$packageFiles = @(
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

. (Join-Path $PSScriptRoot "runtime-payload.ps1")
Copy-RuntimePayload -BuildOutput $output -Destination $stage -AssetsFile (Join-Path $projectRoot "obj\rh$RhinoMajorVersion\project.assets.json") -TargetFramework $TargetFramework

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

$staleYakPattern = "rhinoscanfly-$version-rh${RhinoMajorVersion}_*-win.yak"

Get-ChildItem -LiteralPath $dist -Filter $staleYakPattern -File |
    Remove-Item -Force

$yakPackage = Join-Path $dist $yakPackages[0].Name
Copy-Item -LiteralPath $yakPackages[0].FullName -Destination $yakPackage -Force

if ($Publish -eq "Test") {
    & (Join-Path $projectRoot 'tools\manual\check-runtime-payload.ps1') -RhinoVersion $RhinoMajorVersion
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    & $YakPath push --source "https://test.yak.rhino3d.com" $yakPackage
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}
elseif ($Publish -eq "Production") {
    & (Join-Path $projectRoot 'tools\manual\check-runtime-payload.ps1') -RhinoVersion $RhinoMajorVersion
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    & $YakPath push $yakPackage
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

Write-Host "Manual ZIP: $zip"
Write-Host "Yak package: $yakPackage"
