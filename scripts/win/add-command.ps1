[CmdletBinding()]
param(
    [Parameter(Mandatory = $true, Position = 0)]
    [string] $Name
)

$ErrorActionPreference = "Stop"
$repoRoot = [IO.Path]::GetFullPath((Split-Path -Parent (Split-Path -Parent $PSScriptRoot)))
$commandNameScript = Join-Path $PSScriptRoot "command-name.ps1"
. $commandNameScript
$command = Resolve-RhinoCommandName -Name $Name
$projects = @(Get-ChildItem -LiteralPath $repoRoot -Filter "*.fsproj" -File)

if ($projects.Count -ne 1) {
    throw "Expected one .fsproj in '$repoRoot', found $($projects.Count)."
}

$project = $projects[0].FullName
$projectContent = [IO.File]::ReadAllText($project)
$projectXml = [xml] $projectContent
$rootNamespaceNode = $projectXml.SelectSingleNode("/Project/PropertyGroup/RootNamespace")

if ($null -eq $rootNamespaceNode -or [string]::IsNullOrWhiteSpace($rootNamespaceNode.InnerText)) {
    throw "RootNamespace was not found in '$project'."
}

$rootNamespace = $rootNamespaceNode.InnerText.Trim()
$relativeCommandPath = $command.RelativePath
$commandPath = Join-Path $repoRoot $relativeCommandPath
$registryPath = Join-Path $repoRoot "src\Commands\Commands.fs"
$addFileScript = Join-Path $PSScriptRoot "add-file.ps1"

if (Test-Path -LiteralPath $commandPath) {
    throw "Command file already exists: '$commandPath'."
}

if (-not (Test-Path -LiteralPath $registryPath -PathType Leaf)) {
    throw "Rhino command wrappers were not found: '$registryPath'."
}

if (-not (Test-Path -LiteralPath $addFileScript -PathType Leaf)) {
    throw "Source registration script was not found: '$addFileScript'."
}

$registryContent = [IO.File]::ReadAllText($registryPath)
$typeName = $command.TypeName
$escapedTypeName = [regex]::Escape($typeName)

if ([regex]::IsMatch($registryContent, "(?m)^type\s+$escapedTypeName\s*\(")) {
    throw "Command '$($command.CommandName)' is already registered in Commands.fs."
}

$guid = [guid]::NewGuid().ToString().ToUpperInvariant()
$newline = if ($registryContent.Contains("`r`n")) { "`r`n" } else { "`n" }
$source = @"
module $rootNamespace.$($command.ModuleSuffix)

open global.$rootNamespace
open Rhino
open Rhino.Commands

let run (_document: RhinoDoc) =
    RhinoApp.WriteLine "$($command.CommandName)"
    Result.Success
"@
$registration = @"
[<Guid("$guid")>]
[<CommandStyle(Style.Transparent)>]
type $typeName() =
    inherit PluginCommand($($command.ModuleSuffix).run)
"@

$updatedRegistry = $registryContent.TrimEnd() + $newline + $newline + $registration.Trim() + $newline
$utf8WithoutBom = [Text.UTF8Encoding]::new($false)
$projectUpdated = $false

try {
    [IO.File]::WriteAllText($commandPath, $source.Trim() + $newline, $utf8WithoutBom)
    & $addFileScript -Name $relativeCommandPath
    $projectUpdated = $true
    [IO.File]::WriteAllText($registryPath, $updatedRegistry, $utf8WithoutBom)
}
catch {
    if ($projectUpdated) {
        [IO.File]::WriteAllText($project, $projectContent, $utf8WithoutBom)
    }

    [IO.File]::WriteAllText($registryPath, $registryContent, $utf8WithoutBom)

    if (Test-Path -LiteralPath $commandPath) {
        Remove-Item -LiteralPath $commandPath -Force
    }

    throw
}

Write-Host "Created $relativeCommandPath"
Write-Host "Registered Rhino command $($command.CommandName) with GUID $guid."
Write-Host "Edit the run function in that file."
