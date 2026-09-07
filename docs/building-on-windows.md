# Building on Windows

Install Rhino and the [.NET 10 SDK](https://dotnet.microsoft.com/en-us/download/dotnet/10.0).

Use PowerShell 7.4 or newer (`pwsh`) for packaging. Building and installing also work in Windows PowerShell 5.1. Rhino 8 builds require its .NET 8 runtime mode.

Run (set your version)

```powershell
.\scripts\win\format.ps1 -Check
.\scripts\win\check.ps1
.\scripts\win\build.ps1 -RhinoVersion 8
.\scripts\win\build-all.ps1
```

For a local build, close Rhino and run:

```powershell
.\build-and-install.ps1
```

It skips the checks, installs to `bin\RhinosCanFlyDev`, then starts Rhino. Add `-RhinoVersion 8` to choose a version.

Uninstall the Package Manager version before using a dev build.

## Adding a command

```powershell
.\scripts\win\add-command.ps1 -Name MyNewCommand
```

Then edit `src\Commands\MyNewCommand.fs`.

## Yak packages

Login:

```powershell
$yak = "C:\Program Files\Rhino 8\System\yak.exe"
& $yak login --source https://test.yak.rhino3d.com
```

Build a package in `dist`:

```powershell
.\scripts\win\yak.ps1 -RhinoVersion 8
```

Add `-Publish Test` for the test server or `-Publish Production` for the real one.
