# Never do this

Don't cancel mouse Down/Up with `Rhino.UI.MouseCallback`. This caused white Rhino windows, frozen panels and a dead command line.

`WH_MOUSE` owns the pair. Only take Down when the target window and the window under the cursor are the same viewport, and any mouse capture belongs to it.

If Down is ours, Up is ours. Don't fake either half or switch owners halfway through.

Normal click-to-fly waits for Up and for viewport capture to finish. Hold-to-fly is the only flight entry that starts while RMB is down.

The hook records input and wakes the UI. No RhinoCommon or `RunScript` inside it.
