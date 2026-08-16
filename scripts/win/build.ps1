param(
    [ValidateSet("Debug", "Release")]
    [string] $Configuration = "Release",
    [switch] $Clean,
    [switch] $SkipChecks,
    [switch] $SkipSetup,
    [switch] $Quiet,
    [int] $RhinoVersion = 0
)

$ErrorActionPreference = "Stop"
$projectRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$buildSetup = Join-Path $PSScriptRoot "build-setup.ps1"
$projects = @(Get-ChildItem -LiteralPath $projectRoot -Filter "*.fsproj" -File)

if ($projects.Count -ne 1) {
    throw "Expected one .fsproj in '$projectRoot', found $($projects.Count)."
}

$project = $projects[0].FullName
$pluginName = $projects[0].BaseName

function Invoke-DotNet {
    param([string[]] $Arguments)

    if ($Quiet) {
        $output = & dotnet @Arguments 2>&1
        $exitCode = $LASTEXITCODE

        if ($exitCode -ne 0) {
            $output | ForEach-Object { Write-Host $_ }
        }
    }
    else {
        & dotnet @Arguments
        $exitCode = $LASTEXITCODE
    }

    if ($exitCode -ne 0) { exit $exitCode }
}

$buildSetupParameters = @{ Quiet = $Quiet.IsPresent }

if ($PSBoundParameters.ContainsKey("RhinoVersion")) {
    $buildSetupParameters.RhinoVersion = $RhinoVersion
}

if (-not $SkipSetup) {
    . $buildSetup @buildSetupParameters
}

$properties = @(
    "-p:RhinoMajorVersion=$RhinoMajorVersion"
    "-p:TargetFramework=$TargetFramework"
    "-p:RhinoCommonPackageVersion=$RhinoCommonPackageVersion"
)

if ($SkipChecks) {
    $properties += "-p:RunSourceChecks=false"
}

$verbosityArguments = @()

if ($Quiet) {
    $verbosityArguments += @("--nologo", "--verbosity", "quiet")
}

if ($Clean) {
    Invoke-DotNet (@("restore", $project) + $properties + $verbosityArguments)
    Invoke-DotNet (@("clean", $project, "--configuration", $Configuration) + $properties + $verbosityArguments)
}

$buildArguments = @("build", $project, "--configuration", $Configuration) + $properties

if ($Clean) {
    $buildArguments += "--no-restore"
}

if ($Quiet) {
    $buildArguments += @("--nologo", "--verbosity", "quiet")
}

Invoke-DotNet $buildArguments

if (-not $Quiet) {
    $output = Join-Path $projectRoot "bin\rh$RhinoMajorVersion\$Configuration\$TargetFramework\$pluginName.rhp"
    Write-Host "Built: $output"
}
