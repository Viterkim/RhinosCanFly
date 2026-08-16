[CmdletBinding()]
param(
    [Parameter(Mandatory = $true, Position = 0)]
    [ValidateNotNullOrEmpty()]
    [string] $Name,

    [string] $Before = ""
)

$ErrorActionPreference = "Stop"
$repoRoot = [IO.Path]::GetFullPath((Split-Path -Parent (Split-Path -Parent $PSScriptRoot)))
$rootPrefix = $repoRoot.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
$projects = @(Get-ChildItem -LiteralPath $repoRoot -Filter "*.fsproj" -File)

if ($projects.Count -ne 1) {
    throw "Expected one .fsproj in '$repoRoot', found $($projects.Count)."
}

$requestedPath = $Name.Trim()
$sourcePath =
    if ([IO.Path]::IsPathRooted($requestedPath)) {
        [IO.Path]::GetFullPath($requestedPath)
    }
    else {
        [IO.Path]::GetFullPath((Join-Path $repoRoot $requestedPath))
    }

if (-not $sourcePath.StartsWith($rootPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Source file must stay inside '$repoRoot'."
}

if (-not [string]::Equals([IO.Path]::GetExtension($sourcePath), ".fs", [StringComparison]::OrdinalIgnoreCase)) {
    throw "Only .fs source files can be added, not '$sourcePath'."
}

if (-not (Test-Path -LiteralPath $sourcePath -PathType Leaf)) {
    throw "Source file was not found: '$sourcePath'."
}

$relativePath = $sourcePath.Substring($rootPrefix.Length).Replace('\', '/')
$firstDirectory = $relativePath.Split('/')[0]

if ($firstDirectory -in @(".git", "bin", "dist", "obj")) {
    throw "Refusing to compile a generated or repository-internal file: '$relativePath'."
}

$project = $projects[0].FullName
$content = [IO.File]::ReadAllText($project)
$projectXml = [xml] $content
$existingIncludes = @(
    $projectXml.Project.ItemGroup.Compile |
        ForEach-Object { ([string] $_.Include).Replace('\', '/') }
)

if ($existingIncludes -contains $relativePath) {
    throw "'$relativePath' is already included in '$($projects[0].Name)'."
}

$beforeRelativePath =
    if ([string]::IsNullOrWhiteSpace($Before)) {
        "src/Commands/Commands.fs"
    }
    else {
        $requestedBefore = $Before.Trim()

        $beforePath =
            if ([IO.Path]::IsPathRooted($requestedBefore)) {
                [IO.Path]::GetFullPath($requestedBefore)
            }
            else {
                [IO.Path]::GetFullPath((Join-Path $repoRoot $requestedBefore))
            }

        if (-not $beforePath.StartsWith($rootPrefix, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Before must stay inside '$repoRoot'."
        }

        if (-not [string]::Equals([IO.Path]::GetExtension($beforePath), ".fs", [StringComparison]::OrdinalIgnoreCase)) {
            throw "Before must name an F# source file, not '$beforePath'."
        }

        $beforePath.Substring($rootPrefix.Length).Replace('\', '/')
    }

$compileMatches = [regex]::Matches($content, '(?m)^(?<indent>[ \t]*)<Compile\b[^>]*\/>[ \t]*$')

if ($compileMatches.Count -eq 0) {
    throw "No compile list was found in '$project'."
}

$beforeMatches = @(
    $compileMatches |
        Where-Object {
            $include = [regex]::Match($_.Value, 'Include="(?<path>[^"]+)"')

            $include.Success -and
            [string]::Equals(
                $include.Groups["path"].Value.Replace('\', '/'),
                $beforeRelativePath,
                [StringComparison]::OrdinalIgnoreCase
            )
        }
)

if ($beforeMatches.Count -ne 1) {
    throw "Expected one '$beforeRelativePath' compile entry in '$project', found $($beforeMatches.Count)."
}

$newline = if ($content.Contains("`r`n")) { "`r`n" } else { "`n" }
$escapedPath = [Security.SecurityElement]::Escape($relativePath)
$beforeCompile = $beforeMatches[0]
$entry = "$($beforeCompile.Groups['indent'].Value)<Compile Include=`"$escapedPath`" />$newline"
$updated = $content.Insert($beforeCompile.Index, $entry)
$utf8WithoutBom = [Text.UTF8Encoding]::new($false)
[IO.File]::WriteAllText($project, $updated, $utf8WithoutBom)

Write-Host "Added $relativePath before $beforeRelativePath."
