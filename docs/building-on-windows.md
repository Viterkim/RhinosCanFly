# Building on Windows

Install Rhino.

Get the newest .NET SDK.

[.NET SDK 10](https://dotnet.microsoft.com/en-us/download/dotnet/10.0)

Run (set your version)

```powershell
.\scripts\win\format.ps1 -Check
.\scripts\win\check.ps1
.\scripts\win\build.ps1 -RhinoVersion 8
.\scripts\win\build-all.ps1
```

Or for debugging / installing the build locally. `build-and-install.ps1` builds and registers a copy in `bin\RhinosCanFlyDev` as the current Windows user. Close Rhino, then the script builds, installs the addon and runs Rhino.

```powershell
.\build-and-install.ps1
```

`build-and-install.ps1` defaults to Rhino 9 and skips formatting and source checks for a quicker edit/install loop. Pass `-RhinoVersion 7` or `-RhinoVersion 8` when needed. You can also set `$env:RCF_RHINO_VERSION = "8"` etc.

Uninstall the Package Manager version before using a dev registration. The dev installer overwrites Rhino's registration for the same plugin GUID, but a later Package Manager update or uninstall could replace or remove that registration.

## Adding a command

```powershell
.\scripts\win\add-command.ps1 -Name MyNewCommand
```

Edit `src\Commands\MyNewCommand.fs`. The helper has already created the Rhino wrapper, GUID, and project entry.

## Yak package building and publishing (the built in package manager in rhino)

First login

```powershell
$yak = "C:\Program Files\Rhino 8\System\yak.exe"
& $yak login --source https://test.yak.rhino3d.com
```

Then to build, this makes `dist`, if you add `-Publish Test` you push to the Yak test/staging servers, and `-Publish Production` pushes to the real production servers.

```powershell
.\scripts\win\yak.ps1 -RhinoVersion 8
```
