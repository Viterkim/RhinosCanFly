param(
    [ValidateSet("Debug", "Release")]
    [string] $Configuration = "Release",
    [switch] $Clean,
    [int] $RhinoVersion = 0
)

$ErrorActionPreference = "Stop"
$scriptsDir = Join-Path $PSScriptRoot "scripts\win"
$buildSetup = Join-Path $scriptsDir "build-setup.ps1"
$project = Join-Path $PSScriptRoot "RhinosCanFly.fsproj"

$buildSetupParameters = @{}

if ($PSBoundParameters.ContainsKey("RhinoVersion")) {
    $buildSetupParameters.RhinoVersion = $RhinoVersion
}

. $buildSetup @buildSetupParameters

$properties = @(
    "-p:RhinoMajorVersion=$RhinoMajorVersion"
    "-p:TargetFramework=$TargetFramework"
    "-p:RhinoCommonPackageVersion=$RhinoCommonPackageVersion"
)

if ($Clean) {
    dotnet clean $project --configuration $Configuration @properties
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

    dotnet restore $project @properties
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

$buildArguments = @()

if ($Clean) {
    $buildArguments += "--no-restore"
}

dotnet build $project --configuration $Configuration @properties @buildArguments
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$output = Join-Path $PSScriptRoot "bin\rh$RhinoMajorVersion\$Configuration\$TargetFramework\RhinosCanFly.rhp"
Write-Host "Built: $output"
