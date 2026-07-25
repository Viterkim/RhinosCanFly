[CmdletBinding(DefaultParameterSetName = "Get")]
param(
    [Parameter(Mandatory = $true, ParameterSetName = "Get")]
    [switch] $Get,

    [Parameter(Mandatory = $true, Position = 0, ParameterSetName = "Set")]
    [ValidatePattern('^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$')]
    [string] $Set
)

$ErrorActionPreference = "Stop"
$projectRoot = Split-Path -Parent $PSScriptRoot
$manifestPath = Join-Path $projectRoot "manifest.yml"
$assemblyInfoPath = Join-Path $projectRoot "src\AssemblyInfo.fs"

$manifestPattern = '^(version:\s*)(\d+\.\d+\.\d+)(\s*)$'
$assemblyVersionPattern = '^(.*AssemblyVersion\(")(\d+\.\d+\.\d+\.\d+)("\).*)$'
$assemblyFileVersionPattern = '^(.*AssemblyFileVersion\(")(\d+\.\d+\.\d+\.\d+)("\).*)$'
$assemblyInformationalVersionPattern = '^(.*AssemblyInformationalVersion\(")(\d+\.\d+\.\d+)("\).*)$'

function Get-SingleVersionMatch {
    param(
        [string] $Content,
        [string] $Pattern,
        [string] $Description
    )

    $regex = [regex]::new($Pattern, [Text.RegularExpressions.RegexOptions]::Multiline)
    $matches = $regex.Matches($Content)

    if ($matches.Count -ne 1) {
        throw "Expected exactly one $Description declaration, found $($matches.Count)."
    }

    return $matches[0]
}

function Get-VersionState {
    $manifest = [IO.File]::ReadAllText($manifestPath)
    $assemblyInfo = [IO.File]::ReadAllText($assemblyInfoPath)

    $manifestMatch = Get-SingleVersionMatch $manifest $manifestPattern "manifest version"
    $assemblyVersionMatch = Get-SingleVersionMatch $assemblyInfo $assemblyVersionPattern "AssemblyVersion"
    $assemblyFileVersionMatch = Get-SingleVersionMatch $assemblyInfo $assemblyFileVersionPattern "AssemblyFileVersion"

    $assemblyInformationalVersionMatch =
        Get-SingleVersionMatch $assemblyInfo $assemblyInformationalVersionPattern "AssemblyInformationalVersion"

    return [PSCustomObject]@{
        ManifestContent = $manifest
        AssemblyInfoContent = $assemblyInfo
        ManifestVersion = $manifestMatch.Groups[2].Value
        AssemblyVersion = $assemblyVersionMatch.Groups[2].Value
        AssemblyFileVersion = $assemblyFileVersionMatch.Groups[2].Value
        AssemblyInformationalVersion = $assemblyInformationalVersionMatch.Groups[2].Value
    }
}

function Show-VersionState {
    param([PSCustomObject] $State)

    Write-Host $manifestPath
    Write-Host "  manifest version: $($State.ManifestVersion)"
    Write-Host $assemblyInfoPath
    Write-Host "  AssemblyVersion: $($State.AssemblyVersion)"
    Write-Host "  AssemblyFileVersion: $($State.AssemblyFileVersion)"
    Write-Host "  AssemblyInformationalVersion: $($State.AssemblyInformationalVersion)"

    $expectedAssemblyVersion = "$($State.ManifestVersion).0"

    if (
        $State.AssemblyVersion -ne $expectedAssemblyVersion `
        -or $State.AssemblyFileVersion -ne $expectedAssemblyVersion `
        -or $State.AssemblyInformationalVersion -ne $State.ManifestVersion
    ) {
        Write-Warning "The release version declarations do not agree."
    }
}

function Replace-Version {
    param(
        [string] $Content,
        [string] $Pattern,
        [string] $Replacement
    )

    return [regex]::Replace(
        $Content,
        $Pattern,
        $Replacement,
        [Text.RegularExpressions.RegexOptions]::Multiline
    )
}

$state = Get-VersionState

if ($PSCmdlet.ParameterSetName -eq "Get") {
    Show-VersionState $state
    return
}

$versionParts = $Set.Split(".")

foreach ($part in $versionParts) {
    $value = 0

    if (-not [int]::TryParse($part, [ref] $value) -or $value -gt 65534) {
        throw "Each version component must be between 0 and 65534."
    }
}

$assemblyVersion = "$Set.0"

$updatedManifest =
    Replace-Version $state.ManifestContent $manifestPattern ('${1}' + $Set + '${3}')

$updatedAssemblyInfo =
    Replace-Version $state.AssemblyInfoContent $assemblyVersionPattern ('${1}' + $assemblyVersion + '${3}')

$updatedAssemblyInfo =
    Replace-Version $updatedAssemblyInfo $assemblyFileVersionPattern ('${1}' + $assemblyVersion + '${3}')

$updatedAssemblyInfo =
    Replace-Version $updatedAssemblyInfo $assemblyInformationalVersionPattern ('${1}' + $Set + '${3}')

$utf8WithoutBom = [Text.UTF8Encoding]::new($false)
$updatedPaths = @()

if ($updatedManifest -ne $state.ManifestContent) {
    [IO.File]::WriteAllText($manifestPath, $updatedManifest, $utf8WithoutBom)
    $updatedPaths += $manifestPath
}

if ($updatedAssemblyInfo -ne $state.AssemblyInfoContent) {
    [IO.File]::WriteAllText($assemblyInfoPath, $updatedAssemblyInfo, $utf8WithoutBom)
    $updatedPaths += $assemblyInfoPath
}

if ($updatedPaths.Count -eq 0) {
    Write-Host "Version is already $Set."
}
else {
    Write-Host "Version set to $Set."
}

Show-VersionState (Get-VersionState)
