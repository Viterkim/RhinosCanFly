param(
    [ValidateRange(7, 99)]
    [int] $RhinoVersion = 9,

    [ValidateRange(100, 60000)]
    [int] $IntervalMilliseconds = 500,

    [string] $OutputPath = ""
)

$ErrorActionPreference = "Stop"
$registryPath = "HKLM:\SOFTWARE\McNeel\Rhinoceros\$RhinoVersion.0\Install"
$installPath = (Get-ItemProperty -LiteralPath $registryPath -ErrorAction Stop).InstallPath
$rhinoPath = [IO.Path]::GetFullPath((Join-Path $installPath "System\Rhino.exe"))

$process = @(
    Get-Process -Name "Rhino" -ErrorAction SilentlyContinue |
        Where-Object {
            try {
                [string]::Equals(
                    [IO.Path]::GetFullPath($_.Path),
                    $rhinoPath,
                    [StringComparison]::OrdinalIgnoreCase
                )
            }
            catch {
                $false
            }
        } |
        Sort-Object StartTime -Descending
)[0]

if ($null -eq $process) {
    throw "Rhino $RhinoVersion is not running."
}

if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $projectRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
    $diagnosticsDirectory = Join-Path $projectRoot "obj\diagnostics"
    New-Item -ItemType Directory -Path $diagnosticsDirectory -Force | Out-Null
    $timestamp = Get-Date -Format "yyyyMMdd-HHmmss"
    $OutputPath = Join-Path $diagnosticsDirectory "rhino$RhinoVersion-resources-$timestamp.csv"
}
else {
    $OutputPath = [IO.Path]::GetFullPath($OutputPath)
    $outputDirectory = Split-Path -Parent $OutputPath

    if (-not [string]::IsNullOrWhiteSpace($outputDirectory)) {
        New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
    }
}

if (-not ("RhinosCanFly.NativeResourceCounters" -as [type])) {
    Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;

namespace RhinosCanFly
{
    public static class NativeResourceCounters
    {
        [DllImport("user32.dll")]
        public static extern uint GetGuiResources(IntPtr process, uint flags);
    }
}
"@
}

$process.Refresh()

$clock = [Diagnostics.Stopwatch]::StartNew()
$initialPrivateBytes = $process.PrivateMemorySize64
$initialHandles = $process.HandleCount
$initialThreads = $process.Threads.Count
$initialGdiObjects = [RhinosCanFly.NativeResourceCounters]::GetGuiResources($process.Handle, 0)
$initialUserObjects = [RhinosCanFly.NativeResourceCounters]::GetGuiResources($process.Handle, 1)
$fileVersion = $process.MainModule.FileVersionInfo.FileVersion

Write-Host "Monitoring Rhino $RhinoVersion $fileVersion process $($process.Id). Press Ctrl+C to stop."
Write-Host "CSV: $OutputPath"
Write-Host "Reproduce the problem. Watch what keeps growing."

while (-not $process.HasExited) {
    $process.Refresh()

    $gdiObjects = [RhinosCanFly.NativeResourceCounters]::GetGuiResources($process.Handle, 0)
    $userObjects = [RhinosCanFly.NativeResourceCounters]::GetGuiResources($process.Handle, 1)

    $sample = [PSCustomObject]@{
        Timestamp = (Get-Date).ToString("o")
        ElapsedSeconds = [math]::Round($clock.Elapsed.TotalSeconds, 3)
        RhinoVersion = $fileVersion
        ProcessId = $process.Id
        PrivateMB = [math]::Round($process.PrivateMemorySize64 / 1MB, 2)
        PrivateDeltaMB = [math]::Round(($process.PrivateMemorySize64 - $initialPrivateBytes) / 1MB, 2)
        WorkingSetMB = [math]::Round($process.WorkingSet64 / 1MB, 2)
        Handles = $process.HandleCount
        HandleDelta = $process.HandleCount - $initialHandles
        Threads = $process.Threads.Count
        ThreadDelta = $process.Threads.Count - $initialThreads
        GdiObjects = $gdiObjects
        GdiDelta = [int64] $gdiObjects - [int64] $initialGdiObjects
        UserObjects = $userObjects
        UserDelta = [int64] $userObjects - [int64] $initialUserObjects
    }

    $sample | Export-Csv -LiteralPath $OutputPath -NoTypeInformation -Append

    Write-Host (
        "{0:HH:mm:ss}  private {1,8:N1} MB ({2,8:+0.0;-0.0;0.0})  handles {3,6} ({4,5:+0;-0;0})  USER {5,6} ({6,5:+0;-0;0})  GDI {7,6} ({8,5:+0;-0;0})" -f
            (Get-Date),
            $sample.PrivateMB,
            $sample.PrivateDeltaMB,
            $sample.Handles,
            $sample.HandleDelta,
            $sample.UserObjects,
            $sample.UserDelta,
            $sample.GdiObjects,
            $sample.GdiDelta
    )

    Start-Sleep -Milliseconds $IntervalMilliseconds
}
