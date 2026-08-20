[CmdletBinding()]
param(
    [Parameter(Mandatory = $true, Position = 0)]
    [ValidateNotNullOrEmpty()]
    [string] $Name
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
    throw "Only .fs source files can be removed, not '$sourcePath'."
}

$relativePath = $sourcePath.Substring($rootPrefix.Length).Replace('\', '/')
$firstDirectory = $relativePath.Split('/')[0]

if ($firstDirectory -in @(".git", "bin", "dist", "obj")) {
    throw "Refusing to edit a generated or repository-internal file entry: '$relativePath'."
}

$project = $projects[0].FullName
$content = [IO.File]::ReadAllText($project)
$compileMatches = [regex]::Matches($content, '(?m)^(?<indent>[ \t]*)<Compile\b[^>]*\/>[ \t]*$')
$sourceMatches = @(
    $compileMatches |
        Where-Object {
            $include = [regex]::Match($_.Value, 'Include="(?<path>[^"]+)"')

            $include.Success -and
            [string]::Equals(
                $include.Groups["path"].Value.Replace('\', '/'),
                $relativePath,
                [StringComparison]::OrdinalIgnoreCase
            )
        }
)

if ($sourceMatches.Count -ne 1) {
    throw "Expected one '$relativePath' compile entry in '$($projects[0].Name)', found $($sourceMatches.Count)."
}

$compile = $sourceMatches[0]
$removeLength = $compile.Length
$afterCompile = $compile.Index + $compile.Length

if ($content.Substring($afterCompile).StartsWith("`r`n")) {
    $removeLength += 2
}
elseif ($content.Substring($afterCompile).StartsWith("`n")) {
    $removeLength += 1
}

$updated = $content.Remove($compile.Index, $removeLength)
$utf8WithoutBom = [Text.UTF8Encoding]::new($false)
[IO.File]::WriteAllText($project, $updated, $utf8WithoutBom)

if (Test-Path -LiteralPath $sourcePath -PathType Leaf) {
    Write-Host "Removed $relativePath from $($projects[0].Name). The source file remains on disk."
}
else {
    Write-Host "Removed $relativePath from $($projects[0].Name)."
}
