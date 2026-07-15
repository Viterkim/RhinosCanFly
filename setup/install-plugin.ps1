param(
    [ValidateSet("Debug", "Release")]
    [string] $Configuration = "Release",
    [switch] $Clean,
    [int] $RhinoVersion = 0
)

$ErrorActionPreference = "Stop"

$pluginId = "8E6E7D56-5434-4EF6-884F-6C5130291935"
$pluginName = "RhinosCanFly"
$repoRoot = Split-Path -Parent $PSScriptRoot
$settings = Join-Path $PSScriptRoot "build-settings.ps1"
$buildScript = Join-Path $repoRoot "build.ps1"

$buildParameters = @{
    Configuration = $Configuration
    Clean = $Clean.IsPresent
}

if ($PSBoundParameters.ContainsKey("RhinoVersion")) {
    $buildParameters.RhinoVersion = $RhinoVersion
}

& $buildScript @buildParameters
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

. $settings

$pluginFile = Join-Path $repoRoot "bin\$Configuration\$TargetFramework\RhinosCanFly.rhp"
$registryPath = "HKCU:\Software\McNeel\Rhinoceros\$RhinoMajorVersion.0\Plug-ins\$pluginId"
$pluginRegistryPath = Join-Path $registryPath "PlugIn"

if (-not (Test-Path -LiteralPath $pluginFile)) {
    throw "The build succeeded but '$pluginFile' was not found."
}

New-Item -Path $registryPath -Force | Out-Null
New-Item -Path $pluginRegistryPath -Force | Out-Null
New-ItemProperty -Path $registryPath -Name "Name" -Value $pluginName -PropertyType String -Force | Out-Null
New-ItemProperty -Path $registryPath -Name "FileName" -Value $pluginFile -PropertyType String -Force | Out-Null
New-ItemProperty -Path $registryPath -Name "LoadMode" -Value 1 -PropertyType DWord -Force | Out-Null
New-ItemProperty -Path $pluginRegistryPath -Name "FileName" -Value $pluginFile -PropertyType String -Force | Out-Null

Write-Host "Installed: $pluginFile"
Write-Host "Registered for Rhino $RhinoMajorVersion (current user): $registryPath"

if (Get-Process -Name "Rhino" -ErrorAction SilentlyContinue) {
    Write-Warning "Rhino is running. It cannot hot-reload a loaded .NET plug-in; save your work and restart Rhino to use this build."
} else {
    Write-Host "Start Rhino $RhinoMajorVersion and run RhinosCanFly."
}
