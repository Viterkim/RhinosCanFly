param(
    [ValidateSet("Debug", "Release")]
    [string] $Configuration = "Release",
    [switch] $Clean,
    [int] $RhinoVersion = 0
)

$ErrorActionPreference = "Stop"
$installer = Join-Path $PSScriptRoot "scripts\win\install-plugin.ps1"
$buildSetup = Join-Path $PSScriptRoot "scripts\win\build-setup.ps1"
$setupParameters = @{ Quiet = $true }
$installParameters = @{
    Configuration = $Configuration
    Clean = $Clean.IsPresent
}

if ($PSBoundParameters.ContainsKey("RhinoVersion")) {
    $setupParameters.RhinoVersion = $RhinoVersion
}

. $buildSetup @setupParameters
$installParameters.RhinoVersion = [int] $RhinoMajorVersion

& $installer @installParameters
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$rhinoExecutable = Join-Path $RhinoInstallDir "System\Rhino.exe"

if (-not (Test-Path -LiteralPath $rhinoExecutable)) {
    throw "The build was installed, but Rhino.exe was not found at '$rhinoExecutable'."
}

Write-Host "Starting Rhino $RhinoMajorVersion."
Start-Process `
    -FilePath $rhinoExecutable `
    -WorkingDirectory (Split-Path -Parent $rhinoExecutable)
