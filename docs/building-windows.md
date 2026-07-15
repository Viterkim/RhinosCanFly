# Building on Windows

Install Rhino 8 (or if you are from the future, a newer version).

And you also need a .NET SDK, the time of writing this Rhino 9 was about to come out which targets version 10.

[.Net Sdk 10](https://dotnet.microsoft.com/en-us/download/dotnet/10.0)

```powershell
.\build.ps1
.\build.ps1 -RhinoVersion 8
.\setup\install-plugin.ps1 -RhinoVersion 8
```

No version uses newest Rhino, you can also set `$env:RCF_RHINO_VERSION = "8"` for the current one.

`install-plugin.ps1` builds and registers the dev build as the current windows user.

## Yak version/package

```powershell
.\yak.ps1 -RhinoVersion 8
```

Makes `dist`, add `-Publish Test` to push to the Yak test/staging whatever server or `-Publish Production` for real.


## Yak Publishing

```
$yak = "C:\Program Files\Rhino 8\System\yak.exe"
& $yak login --source https://test.yak.rhino3d.com
```
