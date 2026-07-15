param(
    [int] $RhinoVersion = 0
)

$ErrorActionPreference = "Stop"
$supportedTargets = @{
    8 = "net8.0-windows"
    9 = "net10.0-windows"
}

$requestedVersion = $RhinoVersion

if ($requestedVersion -notin @(0, 8, 9)) {
    throw "RhinoVersion must be 8 or 9, not '$requestedVersion'."
}

if ($requestedVersion -eq 0 -and -not [string]::IsNullOrWhiteSpace($env:RCF_RHINO_VERSION)) {
    $environmentVersion = $env:RCF_RHINO_VERSION.Trim()

    if ($environmentVersion -notin @("8", "9")) {
        throw "RCF_RHINO_VERSION must be 8 or 9, not '$environmentVersion'."
    }

    $requestedVersion = [int] $environmentVersion
}

$versionsToFind = if ($requestedVersion -ne 0) { @($requestedVersion) } else { @(9, 8) }
$rhino = $null

foreach ($major in $versionsToFind) {
    $installPath = $null
    $registry = "HKLM:\SOFTWARE\McNeel\Rhinoceros\$major.0\Install"

    if ($null -ne (Get-PSDrive -Name HKLM -ErrorAction SilentlyContinue) -and (Test-Path -LiteralPath $registry)) {
        $installPath = (Get-ItemProperty -LiteralPath $registry -ErrorAction SilentlyContinue).InstallPath
    }

    if (-not $installPath) {
        $candidate = Join-Path $env:ProgramFiles "Rhino $major"
        if (Test-Path -LiteralPath $candidate) { $installPath = $candidate }
    }

    if ($installPath) {
        foreach ($relative in @("System\netcore", "System")) {
            $system = Join-Path $installPath $relative
            if (Test-Path -LiteralPath (Join-Path $system "RhinoCommon.dll")) {
                $rhino = [PSCustomObject]@{
                    Major = $major
                    Install = $installPath
                    System = $system
                }
                break
            }
        }
    }

    if ($null -ne $rhino) { break }
}

if ($null -eq $rhino) {
    if ($requestedVersion -ne 0) {
        throw "Rhino $requestedVersion was requested but was not found."
    }

    throw "Rhino 8 or 9 was not found. Install Rhino, then run this script again."
}

$targetFramework = $supportedTargets[$rhino.Major]
$yakPath = Join-Path $rhino.Install "System\Yak.exe"
$settings = Join-Path $PSScriptRoot "build-settings.ps1"
$content = @(
    "`$RhinoMajorVersion = `"$($rhino.Major)`""
    "`$RhinoInstallDir = `"$($rhino.Install)`""
    "`$RhinoSystemDir = `"$($rhino.System)`""
    "`$TargetFramework = `"$targetFramework`""
    "`$YakPath = `"$yakPath`""
)

Set-Content -LiteralPath $settings -Value $content -Encoding UTF8

Write-Host "Configured Rhino $($rhino.Major)"
Write-Host "RhinoCommon: $($rhino.System)"
Write-Host "Target: $targetFramework"
