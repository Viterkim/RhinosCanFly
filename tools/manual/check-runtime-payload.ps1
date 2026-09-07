#Requires -Version 7.4

param(
    [ValidateSet(0, 7, 8, 9)][int] $RhinoVersion = 0,
    [string[]] $ArchivePath
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$payload_requested_version = $RhinoVersion
if ($ArchivePath -and $payload_requested_version -eq 0) { throw 'ArchivePath requires a specific RhinoVersion.' }
$payload_version = & (Join-Path $repo 'scripts\win\set-versions.ps1') -Check
. (Join-Path $repo 'scripts\win\runtime-payload.ps1')
. (Join-Path $repo 'scripts\win\build-setup.ps1') -MatrixOnly -Quiet
$payload_versions = if ($payload_requested_version -eq 0) { @($ReleaseRhinoVersions) } else { @($payload_requested_version) }
function Get-ManifestValue {
    param([string] $Content, [string] $Key)
    $pattern = '(?m)^' + [regex]::Escape($Key) + ':\s*(?:"([^"\r\n]+)"|''([^''\r\n]+)''|([^\s#"'']+))[ \t]*(?:#[^\r\n]*)?\r?$'
    $matches = [regex]::Matches($Content, $pattern)
    if ($matches.Count -ne 1) { throw "Expected one manifest '$Key' scalar." }
    foreach ($group in 1..3) {
        if ($matches[0].Groups[$group].Success) { return $matches[0].Groups[$group].Value }
    }
}
$payload_name = Get-ManifestValue ([IO.File]::ReadAllText((Join-Path $repo 'manifest.yml'))) 'name'

function Assert-PluginVersion {
    param([string] $Path, [string] $Version, [string] $Framework, [string] $RhinoPackage)
    $stream = [IO.File]::OpenRead($Path)
    $pe = [Reflection.PortableExecutable.PEReader]::new($stream)
    try {
        $reader = [Reflection.Metadata.PEReaderExtensions]::GetMetadataReader($pe)
        $assembly = $reader.GetAssemblyDefinition()
        if ($reader.GetString($assembly.Name) -cne 'RhinosCanFly') { throw 'The packaged assembly is not RhinosCanFly.' }
        $versions = @{}
        foreach ($handle in $assembly.GetCustomAttributes()) {
            $attribute = $reader.GetCustomAttribute($handle)
            if ($attribute.Constructor.Kind -ne 'MemberReference') { continue }
            $member = $reader.GetMemberReference([Reflection.Metadata.MemberReferenceHandle] $attribute.Constructor)
            if ($member.Parent.Kind -ne 'TypeReference') { continue }
            $type = $reader.GetTypeReference([Reflection.Metadata.TypeReferenceHandle] $member.Parent)
            $name = $reader.GetString($type.Name)
            if ($name -notin @('AssemblyFileVersionAttribute', 'AssemblyInformationalVersionAttribute', 'TargetFrameworkAttribute', 'GuidAttribute')) { continue }
            if ($versions.ContainsKey($name)) { throw "Duplicate plugin version attribute '$name'." }
            $blob = $reader.GetBlobReader($attribute.Value)
            if ($blob.ReadUInt16() -ne 1) { throw "Invalid plugin version attribute '$name'." }
            $versions[$name] = $blob.ReadSerializedString()
        }
        $expectedFramework = if ($Framework -eq 'net48') { '.NETFramework,Version=v4.8' } else {
            '.NETCoreApp,Version=v' + ([regex]::Match($Framework, '^net(\d+\.\d+)')).Groups[1].Value
        }
        $guidMatches = [regex]::Matches([IO.File]::ReadAllText((Join-Path $repo 'src\AssemblyInfo.fs')), 'assembly:\s*Guid\("([^"]+)"\)')
        if ($guidMatches.Count -ne 1 -or $versions['GuidAttribute'] -ine $guidMatches[0].Groups[1].Value) {
            throw 'Packaged plug-in GUID does not match AssemblyInfo.fs.'
        }
        if ($versions['TargetFrameworkAttribute'] -cne $expectedFramework) {
            throw "Packaged plug-in targets '$($versions['TargetFrameworkAttribute'])'; expected '$expectedFramework'."
        }
        $rhinoReferences = @(
            foreach ($referenceHandle in $reader.AssemblyReferences) {
                $reference = $reader.GetAssemblyReference($referenceHandle)
                if ($reader.GetString($reference.Name) -eq 'RhinoCommon') { $reference.Version.ToString() }
            }
        )
        if ($rhinoReferences.Count -ne 1 -or $rhinoReferences[0] -ne ($RhinoPackage -split '-')[0]) {
            throw "Packaged plug-in uses an unexpected RhinoCommon version."
        }
        if ($assembly.Version.ToString() -ne "$Version.0" -or
            $versions['AssemblyFileVersionAttribute'] -cne "$Version.0" -or
            $versions['AssemblyInformationalVersionAttribute'] -cne $Version) {
            throw "Packaged plugin version mismatch; expected $Version ($Version.0)."
        }
    }
    finally { $pe.Dispose(); $stream.Dispose() }
}
$temporary = Join-Path ([IO.Path]::GetTempPath()) ('rcf-payload-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $temporary | Out-Null
try {
    foreach ($payload_major in $payload_versions) {
        . (Join-Path $repo 'scripts\win\build-setup.ps1') -RhinoVersion $payload_major -Quiet
        $output = Join-Path $repo "bin\rh$payload_major\Release\$TargetFramework"
        $stage = Join-Path $temporary "rh$payload_major"
        Copy-RuntimePayload -BuildOutput $output -Destination $stage -AssetsFile (Join-Path $repo "obj\rh$payload_major\project.assets.json") -TargetFramework $TargetFramework
        foreach ($name in @('LICENSE', 'README.md', 'icon.png')) {
            Copy-Item -LiteralPath (Join-Path $repo $name) -Destination $stage
        }
        $expected = @{}
        foreach ($file in Get-ChildItem -LiteralPath $stage -File -Recurse) {
            $relative = $file.FullName.Substring($stage.Length + 1).Replace('\', '/')
            $expected[$relative] = $file
        }
        $expected['manifest.yml'] = $null
        if ($ArchivePath) {
            $paths = $ArchivePath
        }
        else {
            $rhinoMinor = ($RhinoCommonPackageVersion -split '\.')[1]
            $packages = @(Get-ChildItem -LiteralPath (Join-Path $repo 'dist') -Filter "rhinoscanfly-$payload_version-rh$($payload_major)_$rhinoMinor-win.yak" -File)
            if ($packages.Count -ne 1) { throw "Expected one Yak package for Rhino $payload_major." }
            $paths = @($packages[0].FullName, (Join-Path $repo "dist\RhinosCanFly-$payload_version-rh$payload_major-win.zip"))
        }
        foreach ($path in $paths) {
            if ([IO.Path]::GetExtension($path) -eq '.yak') {
                $minor = ($RhinoCommonPackageVersion -split '\.')[1]
                $expectedName = "$($payload_name.ToLowerInvariant())-$payload_version-rh${payload_major}_$minor-win.yak"
                if ([IO.Path]::GetFileName($path) -cne $expectedName) { throw "Unexpected Yak distribution: '$path'." }
            }
            $archive = [IO.Compression.ZipFile]::OpenRead($path)
            try {
                $entries = @{}
                foreach ($entry in $archive.Entries) {
                    $relative = $entry.FullName.Replace('\', '/')
                    if ($relative.EndsWith('/')) { continue }
                    $name = ($relative -split '/')[-1]
                    if ($name -like '*.pdb' -or $name -in @('RhinoCommon.dll', 'Rhino.UI.dll', 'Eto.dll', 'Ed.Eto.dll')) {
                        throw "Forbidden runtime file '$relative' in '$path'."
                    }
                    if ($entries.ContainsKey($relative)) { throw "Duplicate archive entry '$relative' in '$path'." }
                    if (-not $expected.ContainsKey($relative)) { throw "Unexpected archive file '$relative' in '$path'." }
                    $entries[$relative] = $entry
                }
                foreach ($relative in $expected.Keys) {
                    if (-not $entries.ContainsKey($relative)) { throw "Missing '$relative' in '$path'." }
                }
                $reader = [IO.StreamReader]::new($entries['manifest.yml'].Open())
                try { $packagedManifest = $reader.ReadToEnd() }
                finally { $reader.Dispose() }
                if ((Get-ManifestValue $packagedManifest 'name') -cne $payload_name -or
                    (Get-ManifestValue $packagedManifest 'version') -cne $payload_version) {
                    throw "Packaged manifest name/version mismatch in '$path'."
                }
                $plugin = Join-Path $temporary 'packaged.rhp'
                $source = $entries['RhinosCanFly.rhp'].Open()
                $destination = [IO.File]::Create($plugin)
                try { $source.CopyTo($destination) }
                finally { $source.Dispose(); $destination.Dispose() }
                Assert-PluginVersion $plugin $payload_version $TargetFramework $RhinoCommonPackageVersion
                foreach ($relative in $expected.Keys) {
                    if ($relative -eq 'manifest.yml') { continue }
                    $file = $expected[$relative]
                    $stream = $entries[$relative].Open()
                    $hash = [Security.Cryptography.SHA256]::Create()
                    try {
                        $packagedHash = [BitConverter]::ToString($hash.ComputeHash($stream)).Replace('-', '')
                        if ($packagedHash -ne (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash) {
                            throw "Packaged '$relative' differs from the current file in '$path'."
                        }
                    }
                    finally { $stream.Dispose(); $hash.Dispose() }
                }
            }
            finally { $archive.Dispose() }
        }
        Write-Host "Rhino $payload_major archives passed: exact file set, content hashes and release metadata."
    }
}
finally {
    $resolved = [IO.Path]::GetFullPath($temporary)
    $allowedPrefix = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
    if (-not $resolved.StartsWith($allowedPrefix, [StringComparison]::OrdinalIgnoreCase) -or
        -not ([IO.Path]::GetFileName($resolved)).StartsWith('rcf-payload-')) { throw 'Refusing unexpected cleanup path.' }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
