param(
    [ValidateSet("Debug", "Release")]
    [string] $Configuration = "Release",
    [switch] $Clean,
    [int] $RhinoVersion = 0
)

$ErrorActionPreference = "Stop"
$installer = Join-Path $PSScriptRoot "scripts\install-plugin.ps1"
$settings = Join-Path $PSScriptRoot "scripts\build-settings.ps1"
$installParameters = @{
    Configuration = $Configuration
    Clean = $Clean.IsPresent
}

if ($PSBoundParameters.ContainsKey("RhinoVersion")) {
    $installParameters.RhinoVersion = $RhinoVersion
}

$runningRhino = @(Get-Process -Name "Rhino" -ErrorAction SilentlyContinue)

if ($runningRhino.Count -gt 0) {
    throw "Rhino is running. Save your work, close Rhino yourself, then run build-and-install.ps1 again."
}

& $installer @installParameters
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

. $settings
$rhinoExecutable = Join-Path $RhinoInstallDir "System\Rhino.exe"

if (-not (Test-Path -LiteralPath $rhinoExecutable)) {
    throw "The build was installed, but Rhino.exe was not found at '$rhinoExecutable'."
}

Write-Host "Starting Rhino $RhinoMajorVersion."
Start-Process -FilePath $rhinoExecutable
