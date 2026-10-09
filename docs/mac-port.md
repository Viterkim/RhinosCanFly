# Mac port

Rhino 8/9, macOS 14+. No Rhino 7. Rhino 9 needs Apple silicon and an arm64 .NET SDK. The Rhino 8 build includes Intel too.

Mouse and trackpad flying have been tested on Apple silicon, macOS 15, Rhino 8/9. Intel is untested.

Flight uses the native process tap and may need Accessibility permission in Rhino. Captured presses keep their release handling even after flight stops. Try the trackpad with a mouse still connected too. `bash scripts/mac/check-pointer-worker.sh` checks routing, replay and shutdown, then posts input through the actual process tap when permission is available. Missing permission reports a skip. Startup failure with permission fails the check.

Get the SDK from `global.json` and Xcode command-line tools. Close Rhino, then run this from the repo folder:

```sh
bash scripts/mac/build-and-install.sh --rhino-version 8
```

Use `9` for Rhino 9. Add `--rhino-app "/Applications/RhinoWIP.app"` if it can't find it. `--preflight` checks the setup without building/installing, `--help` has the other options.

The build and install script replaces an existing build using its matching Mac Yak archive in `dist/mac`. Keep those archives so it can recover if installing goes wrong. An install made elsewhere needs its old archive supplied with `--rollback-package /path/to/previous.yak`.

For releases: Actions > Build Mac packages > Run workflow. Pick the release branch. Download the result and extract its four files into `dist`, then run `scripts/win/publish-mac.ps1` on Windows. Add `-CheckOnly` to just validate them. The workflow needs to be on the default branch for the button to appear. It doesn't publish anything.
