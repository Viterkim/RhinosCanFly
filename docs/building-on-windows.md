# Building on Windows

Install Rhino.

Get the newest .NET SDK.

[.Net Sdk 10](https://dotnet.microsoft.com/en-us/download/dotnet/10.0)

Run (set your version)
```powershell
.\build.ps1 -RhinoVersion 8
```

Or for debugging / installing the build locally. `build-and-install.ps1` builds and registers the dev build as the current Windows user. Close rhino then the script builds, installs the addon and runs Rhino.
```
.\build-and-install.ps1 -RhinoVersion 8
```

If you don't specify the version, the newest installed Rhino is used. You can also set `$env:RCF_RHINO_VERSION = "8"` etc.

Uninstall the Package Manager version before using a dev registration. The dev installer overwrites Rhino's registration for the same plugin GUID, but a later Package Manager update or uninstall could replace or remove that registration.

## Yak package building and publishing (the built in package manager in rhino)

First login

```
$yak = "C:\Program Files\Rhino 8\System\yak.exe"
& $yak login --source https://test.yak.rhino3d.com
```

Then to build, this makes `dist`, if you add `-Publish Test` you push to the Yak test/staging servers, and `-Publish Production` pushes to the real production servers.

```powershell
.\scripts\yak.ps1 -RhinoVersion 8
```
