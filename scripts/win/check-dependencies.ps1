param()

$ErrorActionPreference = "Stop"
$projectRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$project = Join-Path $projectRoot "RhinosCanFly.fsproj"
$buildSetup = Join-Path $PSScriptRoot "build-setup.ps1"
$failedChecks = [Collections.Generic.List[string]]::new()
$previousRhinoMajorVersion = $env:RhinoMajorVersion

function Invoke-Check {
    param(
        [string] $Name,
        [string[]] $Arguments
    )

    Write-Host ""
    Write-Host $Name
    & dotnet @Arguments

    if ($LASTEXITCODE -ne 0) {
        $failedChecks.Add($Name)
    }
}

try {
    . $buildSetup -Quiet -MatrixOnly

    Write-Host "Dependency check"
    Write-Host "Project: $project"
    Write-Host "Selected SDK: $(& dotnet --version)"
    Write-Host "SDK policy: $(Join-Path $projectRoot 'global.json')"
    Write-Host "RhinoCommon updates may raise the minimum supported Rhino service release."
    Write-Host "Transitive packages are normally updated through their top-level package."

    Invoke-Check -Name "Available .NET SDK and runtime updates" -Arguments @(
        "sdk"
        "check"
    )

    foreach ($major in $BuildRhinoVersions) {
        $properties = Get-RhinoBuildProperties -Major $major
        $env:RhinoMajorVersion = [string] $major

        Write-Host ""
        Write-Host "Rhino $major"
        Write-Host "Target: $($properties.TargetFramework)"
        Write-Host "RhinoCommon baseline: $($properties.RhinoCommonPackageVersion)"

        $outdatedArguments = @(
            "package"
            "list"
            "--project"
            $project
            "--outdated"
            "--include-transitive"
            "--highest-minor"
        )

        if ($major -eq 9) {
            $outdatedArguments += "--include-prerelease"
        }

        Invoke-Check -Name "Rhino $major package updates" -Arguments $outdatedArguments

        Invoke-Check -Name "Rhino $major package vulnerabilities" -Arguments @(
            "package"
            "list"
            "--project"
            $project
            "--vulnerable"
            "--include-transitive"
        )
    }
}
finally {
    $env:RhinoMajorVersion = $previousRhinoMajorVersion
}

if ($failedChecks.Count -gt 0) {
    Write-Host ""
    Write-Warning "Dependency checks failed: $($failedChecks -join ', ')"
    exit 1
}

Write-Host ""
Write-Host "Dependency checks completed."
