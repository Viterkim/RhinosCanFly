# Memory debugging

Start Rhino clean, then monitor the process:

```powershell
.\scripts\win\monitor-rhino-resources.ps1 -RhinoVersion 9
```

Reproduce the problem a bunch of times. The CSV goes in `obj\diagnostics`.

For the managed heap, install this once:

```powershell
dotnet tool install dotnet-gcdump --tool-path .\obj\diagnostics\tools
```

Use the PID printed by the monitor. Take a dump before and after reproducing it:

```powershell
$rhinoPid = 12345
$gcdump = ".\obj\diagnostics\tools\dotnet-gcdump.exe"

& $gcdump collect --process-id $rhinoPid --output .\obj\diagnostics\before.gcdump
# Reproduce the problem.
& $gcdump collect --process-id $rhinoPid --output .\obj\diagnostics\after.gcdump

& $gcdump report .\obj\diagnostics\before.gcdump | Set-Content .\obj\diagnostics\before.txt
& $gcdump report .\obj\diagnostics\after.gcdump | Set-Content .\obj\diagnostics\after.txt
```

`gcdump collect` pauses Rhino and forces a full GC. Compare the two reports. If private memory grows but handles, USER and GDI return near baseline, it is probably managed objects being kept alive.

For a white or frozen Rhino window, start this before testing:

```powershell
.\scripts\win\capture-rhino-hang.ps1 -RhinoVersion 9
```

It waits with ProcDump and writes the dump plus the exact plug-in, PDB, config and source state to `obj\diagnostics\hangs`.

If Rhino is white but ProcDump does not call it hung, use the manual command printed by the script.

If Rhino still accepts commands, run `'_RhinosCanFlyInputDiagnostics`. It writes beside the plug-in config before trying to print anything to the command line.
