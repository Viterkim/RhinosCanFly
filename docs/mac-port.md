# Mac port

Rhino 8/9, macOS 14+. No Rhino 7. Rhino 9 needs Apple silicon and an arm64 .NET SDK. The Rhino 8 build includes Intel too.

Mouse and trackpad flying have been tested on Apple silicon, macOS 15, Rhino 8/9. Intel is untested.

This test build uses the new pointer worker by default. It passes events unchanged, but needs keyboard coverage to keep movement in order, so Rhino may need Accessibility permission. Try the trackpad with a mouse still connected too. Setting `RCF_MAC_INPUT=auto` in Rhino's environment brings back the released GCMouse/AppKit input path for comparison. `bash scripts/mac/check-pointer-worker.sh` checks the queue and shutdown, then opens a test window without installing anything. Permission-dependent checks report when they're skipped.

Get the SDK from `global.json` and Xcode command-line tools. Close Rhino, then run this from the repo folder:

```sh
bash scripts/mac/build-and-install.sh --rhino-version 8
```

Use `9` for Rhino 9. Add `--rhino-app "/Applications/RhinoWIP.app"` if it can't find it. `--preflight` checks the setup without building/installing, `--help` has the other options.

Replacing an installed version add `--replace --rollback-package /path/to/previous.yak`. You need the old Mac package so it can recover if installing goes wrong.

For releases: Actions > Build Mac packages > Run workflow. Pick the release branch. Download the result and extract its four files into `dist`, then run `scripts/win/publish-mac.ps1` on Windows. Add `-CheckOnly` to just validate them. The workflow needs to be on the default branch for the button to appear. It doesn't publish anything.
