# Mac port

Rhino 8/9, macOS 14+. No Rhino 7. Rhino 9 needs Apple silicon and an arm64 .NET SDK. The Rhino 8 build includes Intel too.

Mouse and trackpad flying have been tested on Apple silicon, macOS 15, Rhino 8/9. Intel is untested.

Rhino may need Accessibility permission for flight input.

Get the SDK from `global.json` and Xcode command-line tools. Close Rhino, then run this from the repo folder:

```sh
bash scripts/mac/build-and-install.sh --rhino-version 8
```

Use `9` for Rhino 9. Add `--rhino-app "/path/to/Rhino 9.app"` if it's installed elsewhere. `--preflight` checks the setup without building/installing, `--help` has the other options.

The build and install script replaces an existing build using its matching Mac Yak archive in `dist/mac`. Keep those archives so it can recover if installing goes wrong. An install made elsewhere needs its old archive supplied with `--rollback-package /path/to/previous.yak`.

For releases: Actions > Build Mac packages > Run workflow. Pick the release branch. Download the result and extract its four files into `dist` alongside the Windows packages, then run `scripts/win/build-all-prod-push.ps1`. It publishes both platforms. Use `scripts/win/publish-mac.ps1` to publish Mac separately. Both accept `-CheckOnly` to validate without uploading.
