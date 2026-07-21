param(
    [int] $RhinoVersion = 0,
    [switch] $Quiet,
    [switch] $UseNuGetRhinoCommon
)

$ErrorActionPreference = "Stop"
$projectRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $projectRoot "RhinosCanFly.fsproj"

function Get-RhinoBuildProperties {
    param([int] $Major)

    $arguments = @(
        "msbuild"
        $project
        "-nologo"
        "-verbosity:quiet"
        "-getProperty:RhinoMajorVersion"
        "-getProperty:RhinoSupportedVersions"
        "-getProperty:RhinoExperimentalVersions"
        "-getProperty:RhinoBuildVersions"
        "-getProperty:RhinoReleaseVersions"
        "-getProperty:TargetFramework"
        "-getProperty:RhinoCommonPackageVersion"
    )

    if ($Major -ne 0) {
        $arguments += "-p:RhinoMajorVersion=$Major"
    }

    $output = & dotnet @arguments

    if ($LASTEXITCODE -ne 0) {
        throw "Could not read the Rhino build matrix from '$project'."
    }

    try {
        return (($output -join [Environment]::NewLine) | ConvertFrom-Json).Properties
    }
    catch {
        throw "Could not parse the Rhino build matrix from '$project': $($_.Exception.Message)"
    }
}

function Convert-VersionList {
    param([string] $Value)

    return @(
        $Value.Split(';', [StringSplitOptions]::RemoveEmptyEntries) |
            ForEach-Object { [int] $_.Trim() }
    )
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

$requestedVersion = $RhinoVersion

if ($requestedVersion -eq 0 -and -not [string]::IsNullOrWhiteSpace($env:RCF_RHINO_VERSION)) {
    $environmentVersion = 0

    if (-not [int]::TryParse($env:RCF_RHINO_VERSION.Trim(), [ref] $environmentVersion)) {
        throw "RCF_RHINO_VERSION must be an integer, not '$($env:RCF_RHINO_VERSION)'."
    }

    $requestedVersion = $environmentVersion
}

$properties = Get-RhinoBuildProperties -Major $requestedVersion
$SupportedRhinoVersions = Convert-VersionList $properties.RhinoSupportedVersions
$ExperimentalRhinoVersions = Convert-VersionList $properties.RhinoExperimentalVersions
$BuildRhinoVersions = Convert-VersionList $properties.RhinoBuildVersions
$ReleaseRhinoVersions = Convert-VersionList $properties.RhinoReleaseVersions
$resolvedVersion = [int] $properties.RhinoMajorVersion

if ($resolvedVersion -notin $BuildRhinoVersions) {
    $allowed = $BuildRhinoVersions -join ", "
    throw "RhinoVersion must be one of $allowed, not '$resolvedVersion'."
}

$resolvedTargetFramework = [string] $properties.TargetFramework
$resolvedPackageVersion = [string] $properties.RhinoCommonPackageVersion

if ([string]::IsNullOrWhiteSpace($resolvedTargetFramework)) {
    throw "The Rhino $resolvedVersion build profile has no target framework."
}

if ([string]::IsNullOrWhiteSpace($resolvedPackageVersion)) {
    throw "The Rhino $resolvedVersion build profile has no RhinoCommon package version."
}

$rhino = Find-RhinoInstallation -Major $resolvedVersion

if ($null -eq $rhino) {
    $rhino = [PSCustomObject]@{
        Major = $resolvedVersion
        Install = ""
        System = ""
    }
}

$resolvedUseLocalRhinoCommon =
    -not $UseNuGetRhinoCommon.IsPresent -and -not [string]::IsNullOrWhiteSpace($rhino.System)

$resolvedYakPath = ""

if (-not [string]::IsNullOrWhiteSpace($rhino.Install)) {
    $candidateYak = Join-Path $rhino.Install "System\Yak.exe"
    if (Test-Path -LiteralPath $candidateYak) { $resolvedYakPath = $candidateYak }
}

if ([string]::IsNullOrWhiteSpace($resolvedYakPath)) {
    foreach ($major in @($BuildRhinoVersions | Sort-Object -Descending)) {
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

$RhinoMajorVersion = $resolvedVersion
$RhinoInstallDir = [string] $rhino.Install
$RhinoSystemDir = [string] $rhino.System
$RhinoCommonPackageVersion = $resolvedPackageVersion
$UseLocalRhinoCommon = $resolvedUseLocalRhinoCommon
$TargetFramework = $resolvedTargetFramework
$YakPath = $resolvedYakPath

if (-not $Quiet) {
    Write-Host "Configured Rhino $RhinoMajorVersion"

    if ($UseLocalRhinoCommon) {
        Write-Host "RhinoCommon: $RhinoSystemDir"
    }
    else {
        Write-Host "RhinoCommon: NuGet $RhinoCommonPackageVersion"
    }

    Write-Host "Target: $TargetFramework"
}
