param(
    [ValidateSet("Debug", "Release")]
    [string] $Configuration = "Release",
    [switch] $Clean,
    [int] $RhinoVersion = 0
)

$ErrorActionPreference = "Stop"
$scriptsDir = Join-Path $PSScriptRoot "scripts"
$settings = Join-Path $scriptsDir "build-settings.ps1"
$buildSetup = Join-Path $scriptsDir "build-setup.ps1"
$project = Join-Path $PSScriptRoot "RhinosCanFly.fsproj"

$buildSetupParameters = @{}

if ($PSBoundParameters.ContainsKey("RhinoVersion")) {
    $buildSetupParameters.RhinoVersion = $RhinoVersion
}

& $buildSetup @buildSetupParameters

. $settings

if ($UseLocalRhinoCommon) {
    $rhinoCommon = Join-Path $RhinoSystemDir "RhinoCommon.dll"

    if (-not (Test-Path -LiteralPath $rhinoCommon)) {
        throw "RhinoCommon.dll was not found at '$rhinoCommon'. Run .\scripts\build-setup.ps1 again."
    }
}

$properties = @(
    "-p:RhinoMajorVersion=$RhinoMajorVersion"
    "-p:TargetFramework=$TargetFramework"
    "-p:UseLocalRhinoCommon=$UseLocalRhinoCommon"
)

if ($UseLocalRhinoCommon) {
    $properties += "-p:RhinoSystemDir=$RhinoSystemDir"
}

if ($Clean) {
    dotnet restore $project @properties
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

    dotnet clean $project --configuration $Configuration @properties
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

dotnet build $project --configuration $Configuration @properties
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$output = Join-Path $PSScriptRoot "bin\$Configuration\$TargetFramework\RhinosCanFly.rhp"
Write-Host "Built: $output"
