param(
    [int] $RhinoVersion = 0,
    [switch] $Quiet
)

$ErrorActionPreference = "Stop"
$supportedTargets = @{
    7 = "net48"
    8 = "net8.0-windows"
    9 = "net10.0-windows"
}

$requestedVersion = $RhinoVersion

if ($requestedVersion -notin @(0, 7, 8, 9)) {
    throw "RhinoVersion must be 7, 8, or 9, not '$requestedVersion'."
}

if ($requestedVersion -eq 0 -and -not [string]::IsNullOrWhiteSpace($env:RCF_RHINO_VERSION)) {
    $environmentVersion = $env:RCF_RHINO_VERSION.Trim()

    if ($environmentVersion -notin @("7", "8", "9")) {
        throw "RCF_RHINO_VERSION must be 7, 8, or 9, not '$environmentVersion'."
    }

    $requestedVersion = [int] $environmentVersion
}

function Find-RhinoInstallation {
    param([int] $Major)

    $installPath = $null
    $registry = "HKLM:\SOFTWARE\McNeel\Rhinoceros\$Major.0\Install"

    if ($null -ne (Get-PSDrive -Name HKLM -ErrorAction SilentlyContinue) -and (Test-Path -LiteralPath $registry)) {
        $installPath = (Get-ItemProperty -LiteralPath $registry -ErrorAction SilentlyContinue).InstallPath
    }

    if (-not $installPath) {
        $candidate = Join-Path $env:ProgramFiles "Rhino $Major"
        if (Test-Path -LiteralPath $candidate) { $installPath = $candidate }
    }

    if ($installPath) {
        foreach ($relative in @("System\netcore", "System")) {
            $system = Join-Path $installPath $relative
            if (Test-Path -LiteralPath (Join-Path $system "RhinoCommon.dll")) {
                return [PSCustomObject]@{
                    Major = $Major
                    Install = $installPath
                    System = $system
                }
            }
        }
    }

    return $null
}

$versionsToFind = if ($requestedVersion -ne 0) { @($requestedVersion) } else { @(9, 8, 7) }
$rhino = $null

foreach ($major in $versionsToFind) {
    $rhino = Find-RhinoInstallation -Major $major
    if ($null -ne $rhino) { break }
}

if ($null -eq $rhino) {
    if ($requestedVersion -eq 7) {
        $rhino = [PSCustomObject]@{
            Major = 7
            Install = ""
            System = ""
        }
    }
    elseif ($requestedVersion -ne 0) {
        throw "Rhino $requestedVersion was requested but was not found."
    }
    else {
        throw "Rhino 7, 8, or 9 was not found. Install Rhino, then run this script again."
    }
}

$resolvedTargetFramework = $supportedTargets[$rhino.Major]
$resolvedUseLocalRhinoCommon = -not [string]::IsNullOrWhiteSpace($rhino.System)
$resolvedYakPath = ""

if (-not [string]::IsNullOrWhiteSpace($rhino.Install)) {
    $candidateYak = Join-Path $rhino.Install "System\Yak.exe"
    if (Test-Path -LiteralPath $candidateYak) { $resolvedYakPath = $candidateYak }
}

if ([string]::IsNullOrWhiteSpace($resolvedYakPath)) {
    foreach ($major in @(9, 8, 7)) {
        $yakRhino = Find-RhinoInstallation -Major $major

        if ($null -ne $yakRhino) {
            $candidateYak = Join-Path $yakRhino.Install "System\Yak.exe"

            if (Test-Path -LiteralPath $candidateYak) {
                $resolvedYakPath = $candidateYak
                break
            }
        }
    }
}

$RhinoMajorVersion = [int] $rhino.Major
$RhinoInstallDir = [string] $rhino.Install
$RhinoSystemDir = [string] $rhino.System
$UseLocalRhinoCommon = $resolvedUseLocalRhinoCommon
$TargetFramework = $resolvedTargetFramework
$YakPath = $resolvedYakPath

if (-not $Quiet) {
    Write-Host "Configured Rhino $RhinoMajorVersion"

    if ($UseLocalRhinoCommon) {
        Write-Host "RhinoCommon: $RhinoSystemDir"
    }
    else {
        Write-Host "RhinoCommon: NuGet $RhinoMajorVersion compile-only build"
    }

    Write-Host "Target: $TargetFramework"
}
