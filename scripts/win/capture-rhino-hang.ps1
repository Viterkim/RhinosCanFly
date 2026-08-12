param(
    [ValidateRange(7, 99)]
    [int] $RhinoVersion = 9,

    [ValidateRange(0, [int]::MaxValue)]
    [int] $ProcessId = 0,

    [string] $ProcDumpPath = "",

    [string] $PluginPath = "",

    [string] $OutputRoot = ""
)

$ErrorActionPreference = "Stop"
$projectRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$buildSetup = Join-Path $PSScriptRoot "build-setup.ps1"
. $buildSetup -RhinoVersion $RhinoVersion -Quiet

if ([string]::IsNullOrWhiteSpace($RhinoInstallDir)) {
    throw "Rhino $RhinoVersion is not installed."
}

$rhinoPath = [IO.Path]::GetFullPath((Join-Path $RhinoInstallDir "System\Rhino.exe"))

function Find-ProcDump {
    param([string] $RequestedPath)

    if (-not [string]::IsNullOrWhiteSpace($RequestedPath)) {
        $resolved = [IO.Path]::GetFullPath($RequestedPath)

        if (-not (Test-Path -LiteralPath $resolved -PathType Leaf)) {
            throw "ProcDump was not found at '$resolved'."
        }

        return $resolved
    }

    foreach ($name in @("procdump64.exe", "procdump.exe")) {
        $command = Get-Command $name -ErrorAction SilentlyContinue

        if ($null -ne $command) {
            return [IO.Path]::GetFullPath($command.Source)
        }
    }

    foreach ($commonPath in @(
        "C:\Tools\ProcDump\procdump64.exe",
        "C:\Tools\Sysinternals\procdump64.exe",
        "C:\Program Files\SysinternalsSuite\procdump64.exe"
    )) {
        if (Test-Path -LiteralPath $commonPath -PathType Leaf) {
            return $commonPath
        }
    }

    throw "ProcDump was not found. Pass -ProcDumpPath or add procdump64.exe to PATH."
}

function Find-RegisteredPlugin {
    param(
        [int] $Major,
        [string] $PluginId
    )

    $registrations = @(
        "HKCU:\Software\McNeel\Rhinoceros\$Major.0\Plug-ins\$PluginId",
        "HKLM:\Software\McNeel\Rhinoceros\$Major.0\Plug-ins\$PluginId"
    )

    foreach ($registration in $registrations) {
        foreach ($candidateKey in @((Join-Path $registration "PlugIn"), $registration)) {
            $candidate = Get-ItemProperty -LiteralPath $candidateKey -ErrorAction SilentlyContinue

            if ($null -ne $candidate -and -not [string]::IsNullOrWhiteSpace([string] $candidate.FileName)) {
                $candidatePath = [IO.Path]::GetFullPath([string] $candidate.FileName)

                if (Test-Path -LiteralPath $candidatePath -PathType Leaf) {
                    return $candidatePath
                }
            }
        }
    }

    return ""
}

function Write-Utf8Lines {
    param(
        [string] $Path,
        [string[]] $Lines
    )

    [IO.File]::WriteAllLines($Path, $Lines, [Text.UTF8Encoding]::new($false))
}

$processes = @(Get-RunningRhinoProcess -ExecutablePath $rhinoPath | Sort-Object StartTime -Descending)

if ($ProcessId -ne 0) {
    $process = $processes | Where-Object Id -EQ $ProcessId | Select-Object -First 1

    if ($null -eq $process) {
        throw "Process $ProcessId is not the running Rhino $RhinoVersion executable at '$rhinoPath'."
    }
}
else {
    $process = $processes | Select-Object -First 1
}

if ($null -eq $process) {
    throw "Rhino $RhinoVersion is not running."
}

$resolvedProcDump = Find-ProcDump -RequestedPath $ProcDumpPath
$pluginSource = "argument"

if ([string]::IsNullOrWhiteSpace($PluginPath)) {
    $pluginSource = "registered path"

    try {
        $loadedPlugin = @(
            $process.Modules |
                Where-Object { [IO.Path]::GetFileName($_.FileName) -ieq "RhinosCanFly.rhp" }
        ) | Select-Object -First 1

        if ($null -ne $loadedPlugin) {
            $PluginPath = $loadedPlugin.FileName
            $pluginSource = "loaded process module"
        }
    }
    catch {
        Write-Warning "Could not inspect loaded Rhino modules: $($_.Exception.Message)"
    }
}

if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $OutputRoot = Join-Path $projectRoot "obj\diagnostics\hangs"
}
else {
    $OutputRoot = [IO.Path]::GetFullPath($OutputRoot)
}

$timestamp = Get-Date -Format "yyyyMMdd-HHmmss"
$outputDirectory = Join-Path $OutputRoot $timestamp

if (Test-Path -LiteralPath $outputDirectory) {
    $outputDirectory = Join-Path $OutputRoot "$timestamp-$($process.Id)"
}

New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null

$assemblyInfo = Join-Path $projectRoot "src\AssemblyInfo.fs"
$pluginIdMatches = @(
    Select-String -Path $assemblyInfo -Pattern 'assembly:\s*Guid\("([0-9A-Fa-f-]+)"\)' -AllMatches |
        ForEach-Object { $_.Matches }
)

if ($pluginIdMatches.Count -ne 1) {
    throw "Expected one plug-in GUID in '$assemblyInfo', found $($pluginIdMatches.Count)."
}

$pluginId = $pluginIdMatches[0].Groups[1].Value

if ([string]::IsNullOrWhiteSpace($PluginPath)) {
    $PluginPath = Find-RegisteredPlugin -Major $RhinoVersion -PluginId $pluginId
}
else {
    $PluginPath = [IO.Path]::GetFullPath($PluginPath)
}

if ([string]::IsNullOrWhiteSpace($PluginPath) -or -not (Test-Path -LiteralPath $PluginPath -PathType Leaf)) {
    throw "The registered RhinosCanFly plug-in for Rhino $RhinoVersion was not found. Pass its exact path with -PluginPath."
}

$PluginPath = [IO.Path]::GetFullPath($PluginPath)
$pdbPath = [IO.Path]::ChangeExtension($PluginPath, ".pdb")
$capturedPlugin = Join-Path $outputDirectory "RhinosCanFly.rhp"
$capturedPdb = Join-Path $outputDirectory "RhinosCanFly.pdb"
Copy-Item -LiteralPath $PluginPath -Destination $capturedPlugin

$pdbCaptured = Test-Path -LiteralPath $pdbPath -PathType Leaf

if ($pdbCaptured) {
    Copy-Item -LiteralPath $pdbPath -Destination $capturedPdb
}

$gitHead = "unavailable"
$gitStatus = @("unavailable")
$gitDiff = @("unavailable")

try {
    $gitHead = (& git -C $projectRoot rev-parse HEAD 2>&1 | Out-String).Trim()

    if ($LASTEXITCODE -ne 0) {
        throw $gitHead
    }

    $gitStatus = @(& git -C $projectRoot status --short 2>&1)

    if ($LASTEXITCODE -ne 0) {
        throw ($gitStatus -join [Environment]::NewLine)
    }

    $gitDiff = @(& git -C $projectRoot diff HEAD --binary --no-ext-diff 2>&1)

    if ($LASTEXITCODE -ne 0) {
        throw ($gitDiff -join [Environment]::NewLine)
    }
}
catch {
    $gitHead = "unavailable: $($_.Exception.Message)"
    $gitStatus = @("unavailable")
    $gitDiff = @("unavailable")
}

$gitStatusPath = Join-Path $outputDirectory "git-status.txt"
$gitDiffPath = Join-Path $outputDirectory "git-diff.binary.patch"
Write-Utf8Lines -Path $gitStatusPath -Lines $gitStatus
Write-Utf8Lines -Path $gitDiffPath -Lines $gitDiff
$gitDiffHash = (Get-FileHash -LiteralPath $gitDiffPath -Algorithm SHA256).Hash

$untrackedFiles = @()

try {
    $untrackedFiles = @(& git -C $projectRoot ls-files --others --exclude-standard 2>&1)

    if ($LASTEXITCODE -ne 0) {
        throw ($untrackedFiles -join [Environment]::NewLine)
    }
}
catch {
    $untrackedFiles = @("unavailable: $($_.Exception.Message)")
}

$untrackedManifest = Join-Path $outputDirectory "git-untracked.txt"
Write-Utf8Lines -Path $untrackedManifest -Lines $untrackedFiles
$untrackedDirectory = Join-Path $outputDirectory "untracked"

foreach ($relativePath in $untrackedFiles) {
    if (-not $relativePath.StartsWith("unavailable:", [StringComparison]::Ordinal)) {
        $sourcePath = Join-Path $projectRoot $relativePath

        if (Test-Path -LiteralPath $sourcePath -PathType Leaf) {
            $destinationPath = Join-Path $untrackedDirectory $relativePath
            New-Item -ItemType Directory -Path (Split-Path -Parent $destinationPath) -Force | Out-Null
            Copy-Item -LiteralPath $sourcePath -Destination $destinationPath
        }
    }
}

$settingsDirectory = Join-Path $env:APPDATA "McNeel\Rhinoceros\$RhinoVersion.0\settings"
$configPath = Join-Path $settingsDirectory "rhinos-can-fly-config.json"
$diagnosticsPath = Join-Path $settingsDirectory "rhinos-can-fly-input-diagnostics.txt"
$capturedConfig = Join-Path $outputDirectory "rhinos-can-fly-config.json"
$capturedDiagnostics = Join-Path $outputDirectory "rhinos-can-fly-input-diagnostics.txt"
$configCaptured = Test-Path -LiteralPath $configPath -PathType Leaf
$diagnosticsCaptured = Test-Path -LiteralPath $diagnosticsPath -PathType Leaf

if ($configCaptured) {
    Copy-Item -LiteralPath $configPath -Destination $capturedConfig
}

if ($diagnosticsCaptured) {
    Copy-Item -LiteralPath $diagnosticsPath -Destination $capturedDiagnostics
}

$rhinoVersionInfo = [Diagnostics.FileVersionInfo]::GetVersionInfo($rhinoPath)
$pluginVersionInfo = [Diagnostics.FileVersionInfo]::GetVersionInfo($capturedPlugin)
$pluginAssemblyVersion = [Reflection.AssemblyName]::GetAssemblyName($capturedPlugin).Version.ToString()
$pluginHash = (Get-FileHash -LiteralPath $capturedPlugin -Algorithm SHA256).Hash
$pdbHash = if ($pdbCaptured) { (Get-FileHash -LiteralPath $capturedPdb -Algorithm SHA256).Hash } else { "missing" }
$procDumpVersionInfo = [Diagnostics.FileVersionInfo]::GetVersionInfo($resolvedProcDump)

$metadata = [ordered] @{
    CapturedAt = (Get-Date).ToString("o")
    RhinoMajorVersion = $RhinoVersion
    RhinoProcessId = $process.Id
    RhinoProcessStartTime = $process.StartTime.ToString("o")
    RhinoExecutable = $rhinoPath
    RhinoFileVersion = $rhinoVersionInfo.FileVersion
    RhinoProductVersion = $rhinoVersionInfo.ProductVersion
    PluginId = $pluginId
    PluginPath = $PluginPath
    PluginPathSource = $pluginSource
    PluginAssemblyVersion = $pluginAssemblyVersion
    PluginFileVersion = $pluginVersionInfo.FileVersion
    PluginSha256 = $pluginHash
    CapturedPluginPath = $capturedPlugin
    PluginPdbPath = if ($pdbCaptured) { $pdbPath } else { "missing" }
    PluginPdbSha256 = $pdbHash
    CapturedPluginPdbPath = if ($pdbCaptured) { $capturedPdb } else { "missing" }
    GitHead = $gitHead
    GitDiffSha256 = $gitDiffHash
    GitStatusFile = $gitStatusPath
    GitDiffFile = $gitDiffPath
    GitUntrackedManifest = $untrackedManifest
    ConfigPath = if ($configCaptured) { $configPath } else { "missing" }
    DiagnosticsPath = if ($diagnosticsCaptured) { $diagnosticsPath } else { "missing" }
    ProcDumpPath = $resolvedProcDump
    ProcDumpVersion = $procDumpVersionInfo.FileVersion
    ProcDumpArguments = "-accepteula -ma -h -n 1 $($process.Id) $outputDirectory"
}

$metadataPath = Join-Path $outputDirectory "metadata.json"
$metadata | ConvertTo-Json -Depth 3 | Set-Content -LiteralPath $metadataPath -Encoding UTF8

Write-Host "Hang capture: $outputDirectory"
Write-Host "Rhino $RhinoVersion process: $($process.Id)"
Write-Host "Plug-in: $PluginPath"
Write-Warning "A full process dump can contain document data. Keep the capture private."
Write-Host "Waiting for a Rhino window to remain unresponsive long enough for ProcDump to capture it."
Write-Host "If the UI turns white but Windows does not call it hung, stop this and run:"
Write-Host "& `"$resolvedProcDump`" -accepteula -ma $($process.Id) `"$outputDirectory`""

& $resolvedProcDump -accepteula -ma -h -n 1 $process.Id $outputDirectory

if ($LASTEXITCODE -ne 0) {
    throw "ProcDump exited with code $LASTEXITCODE."
}

if (Test-Path -LiteralPath $configPath -PathType Leaf) {
    Copy-Item -LiteralPath $configPath -Destination $capturedConfig -Force
}

if (Test-Path -LiteralPath $diagnosticsPath -PathType Leaf) {
    Copy-Item -LiteralPath $diagnosticsPath -Destination $capturedDiagnostics -Force
}

$dumpFiles = @(
    Get-ChildItem -LiteralPath $outputDirectory -Filter "*.dmp" -File |
        ForEach-Object {
            [ordered] @{
                Path = $_.FullName
                Bytes = $_.Length
            }
        }
)

$metadata["CaptureCompletedAt"] = (Get-Date).ToString("o")
$metadata["DumpFiles"] = $dumpFiles
$metadata | ConvertTo-Json -Depth 3 | Set-Content -LiteralPath $metadataPath -Encoding UTF8

Write-Host "Hang capture completed: $outputDirectory"
