# Building on Windows

Install Rhino and the [.NET 10 SDK](https://dotnet.microsoft.com/en-us/download/dotnet/10.0).

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

For a full release:

```powershell
.\scripts\win\build-all-prod.ps1
.\scripts\win\build-all-prod-push.ps1
```

Put the Mac ZIPs and Yaks for the same version into `dist` before the push command and it publishes those too. An incomplete Mac download stops it before anything uploads. Add `-CheckOnly` to validate without publishing.
