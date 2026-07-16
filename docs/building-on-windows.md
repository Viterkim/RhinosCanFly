# Building on Windows

Install Rhino 8 (or if you are from the future, a newer version).

And you also need a .NET SDK, the time of writing this Rhino 9 was about to come out which targets version 10.

[.Net Sdk 10](https://dotnet.microsoft.com/en-us/download/dotnet/10.0)

```powershell
.\build.ps1
.\build.ps1 -RhinoVersion 8
.\build.ps1 -RhinoVersion 9
.\build-and-install.ps1 -RhinoVersion 8
.\build-and-install.ps1 -RhinoVersion 9
```

If you don't specify the version, the newest installed Rhino is used. You can also set `$env:RCF_RHINO_VERSION = "8"` etc.

`build-and-install.ps1` builds and registers the dev build as the current Windows user. Close rhino then the script builds, installs the addon and runs Rhino.

Uninstall the Package Manager version before using a dev registration. The dev installer overwrites Rhino's registration for the same plugin GUID, but a later Package Manager update or uninstall could replace or remove that registration.

## Yak version / package

```powershell
.\yak.ps1 -RhinoVersion 8
```

Makes `dist`, add `-Publish Test` to push to the Yak test/staging whatever server or `-Publish Production` for real.


## Yak Publishing

```
$yak = "C:\Program Files\Rhino 8\System\yak.exe"
& $yak login --source https://test.yak.rhino3d.com
```
