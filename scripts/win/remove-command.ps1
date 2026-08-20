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
$commandPath = Join-Path $repoRoot $command.RelativePath
$registryPath = Join-Path $repoRoot "src\Commands\Commands.fs"

if (-not (Test-Path -LiteralPath $commandPath -PathType Leaf)) {
    throw "Command source was not found: '$($command.RelativePath)'."
}

if (-not (Test-Path -LiteralPath $registryPath -PathType Leaf)) {
    throw "Rhino command wrappers were not found: '$registryPath'."
}

$projectContent = [IO.File]::ReadAllText($project)
$registryContent = [IO.File]::ReadAllText($registryPath)
$sourceBytes = [IO.File]::ReadAllBytes($commandPath)
$escapedPath = [regex]::Escape($command.RelativePath)
$compilePattern = "(?m)^[ \t]*<Compile Include=`"$escapedPath`"\s*/>[ \t]*(?:\r?\n)?"
$compileMatches = [regex]::Matches($projectContent, $compilePattern)

if ($compileMatches.Count -ne 1) {
    throw "Expected one '$($command.RelativePath)' compile entry in '$($projects[0].Name)', found $($compileMatches.Count)."
}

$escapedType = [regex]::Escape($command.TypeName)
$escapedModule = [regex]::Escape($command.ModuleSuffix)
$registrationPattern =
    '(?ms)^[ \t]*\[<Guid\("[^"]+"\)>\][ \t]*\r?\n(?:[ \t]*\[<CommandStyle\(Style\.Transparent\)>\][ \t]*\r?\n)?[ \t]*type\s+{0}\(\)\s*=\s*\r?\n[ \t]+inherit\s+PluginCommand\({1}\.run\)[ \t]*(?:\r?\n(?:\r?\n)?)?' -f $escapedType, $escapedModule
$registrationMatches = [regex]::Matches($registryContent, $registrationPattern)

if ($registrationMatches.Count -ne 1) {
    throw "Expected one wrapper for '$($command.CommandName)' in Commands.fs, found $($registrationMatches.Count)."
}

$newline = if ($registryContent.Contains("`r`n")) { "`r`n" } else { "`n" }
$updatedProject = [regex]::Replace($projectContent, $compilePattern, "", 1)
$updatedRegistry = [regex]::Replace($registryContent, $registrationPattern, "", 1).TrimEnd() + $newline
$utf8WithoutBom = [Text.UTF8Encoding]::new($false)

try {
    [IO.File]::WriteAllText($project, $updatedProject, $utf8WithoutBom)
    [IO.File]::WriteAllText($registryPath, $updatedRegistry, $utf8WithoutBom)
    Remove-Item -LiteralPath $commandPath -Force
}
catch {
    [IO.File]::WriteAllText($project, $projectContent, $utf8WithoutBom)
    [IO.File]::WriteAllText($registryPath, $registryContent, $utf8WithoutBom)

    if (-not (Test-Path -LiteralPath $commandPath)) {
        [IO.File]::WriteAllBytes($commandPath, $sourceBytes)
    }

    throw
}

Write-Host "Removed $($command.RelativePath)"
Write-Host "Removed Rhino command $($command.CommandName)."
