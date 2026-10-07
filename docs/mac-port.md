# Mac port

Rhino 8/9, macOS 14+. No Rhino 7. Rhino 9 needs Apple silicon and an arm64 .NET SDK. The Rhino 8 build includes Intel too.

I don't have a Mac, so ye someone still needs to try this. The managed code builds here, the native stuff hasn't been compiled against Apple's SDK or run in Rhino yet.

Get the SDK from `global.json` and Xcode command-line tools. Close Rhino, then run this from the repo folder:

```sh
bash scripts/mac/build-and-install.sh --rhino-version 8
```

Use `9` for Rhino 9. Add `--rhino-app "/Applications/RhinoWIP.app"` if it can't find it. `--preflight` checks the setup without building/installing, `--help` has the other options.

Replacing an installed version add `--replace --rollback-package /path/to/previous.yak`. You need the old Mac package so it can recover if installing goes wrong.

Trackpads don't show up as a GCMouse. With no GCMouse connected the flight falls back to AppKit's accelerated pointer deltas, so the trackpad works but feels different from a raw mouse. Connect the mouse before starting a flight to get raw motion.

If it breaks, run `RhinosCanFlyInputInfo` and send the output.
