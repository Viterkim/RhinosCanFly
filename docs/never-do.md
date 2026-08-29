# Never do this

Don't cancel mouse Down/Up with `Rhino.UI.MouseCallback`. This caused white Rhino windows, frozen panels and a dead command line.

`WH_MOUSE` owns every legacy pair. Raw navigation owns its raw pairs while `RIDEV_NOLEGACY` disables legacy mouse messages. Only take a legacy Down when the target window and the window under the cursor are the same viewport and `GetCapture()` is zero. Capture belonging to the expected host is still existing capture.

If Down is ours, Up is ours. Don't fake either half or switch owners halfway through.

Physical-state polling may observe a missing release. It must not release legacy ownership before `WH_MOUSE` consumes Up. A fresh Down can close an old pair whose Up happened outside Rhino.

Normal click-to-fly waits for Up and for viewport capture to finish. Hold-to-fly is the only flight entry that starts while RMB is down.

The hook records input and wakes the UI. No RhinoCommon or `RunScript` inside it.
