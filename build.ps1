param(
    [ValidateSet("Debug", "Release")]
    [string] $Configuration = "Release",
    [switch] $Clean,
    [int] $RhinoVersion = 0
)

$ErrorActionPreference = "Stop"
$setupDir = Join-Path $PSScriptRoot "setup"
$settings = Join-Path $setupDir "build-settings.ps1"
$setup = Join-Path $setupDir "build-setup.ps1"
$project = Join-Path $PSScriptRoot "RhinosCanFly.fsproj"

$setupParameters = @{}

if ($PSBoundParameters.ContainsKey("RhinoVersion")) {
    $setupParameters.RhinoVersion = $RhinoVersion
}

& $setup @setupParameters

. $settings

$rhinoCommon = Join-Path $RhinoSystemDir "RhinoCommon.dll"

if (-not (Test-Path -LiteralPath $rhinoCommon)) {
    throw "RhinoCommon.dll was not found at '$rhinoCommon'. Run .\setup\build-setup.ps1 again."
}

$properties = @(
    "-p:RhinoSystemDir=$RhinoSystemDir"
    "-p:RhinoMajorVersion=$RhinoMajorVersion"
    "-p:TargetFramework=$TargetFramework"
)

if ($Clean) {
    dotnet clean $project --configuration $Configuration @properties
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

dotnet build $project --configuration $Configuration @properties
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$output = Join-Path $PSScriptRoot "bin\$Configuration\$TargetFramework\RhinosCanFly.rhp"
Write-Host "Built: $output"
