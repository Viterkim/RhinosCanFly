# Mac port

Rhino 8/9, macOS 14+. No Rhino 7. Rhino 9 needs Apple silicon and an arm64 .NET SDK. The Rhino 8 build includes Intel too.

Is built and tried flying with a mouse on macOS 15, Rhino 8/9. The new trackpad path still needs a Mac build and test. For now unplug the mouse before entering flight to use it.

Get the SDK from `global.json` and Xcode command-line tools. Close Rhino, then run this from the repo folder:

```sh
bash scripts/mac/build-and-install.sh --rhino-version 8
```

Use `9` for Rhino 9. Add `--rhino-app "/Applications/RhinoWIP.app"` if it can't find it. `--preflight` checks the setup without building/installing, `--help` has the other options.

Replacing an installed version add `--replace --rollback-package /path/to/previous.yak`. You need the old Mac package so it can recover if installing goes wrong.

If it breaks, run `RhinosCanFlyInputInfo` and send the output.
