# Never do this

The old right-click entry cancelled mouse Down/Up through `Rhino.UI.MouseCallback`. It could leave Rhino with white dialogs, frozen panels and a dead command line. The original fix was in 0.3.1 (`48beed3`). Don't bring that button cancellation back.

Use `WH_MOUSE` for legacy button ownership. The hook records input and wakes the UI. No RhinoCommon or `RunScript` inside it.

If Down is ours, Up is ours, including releases over the title bar or outside the viewport. Don't fake mouse messages or switch owners halfway through a pair. Physical-release polling must not discard an outstanding legacy Up.

Start mouse navigation only when the hook target and cursor window resolve to the same viewport and `GetCapture()` is zero. Capture belonging to that viewport is still Rhino's existing interaction.

`RIDEV_NOLEGACY` suppresses legacy mouse messages. Keep button ownership intact across raw startup and shutdown. Never let Rhino receive a Down whose Up we suppress. Finish cleanup even if the worker returns late, so fresh clicks aren't swallowed forever.

Click-to-fly waits for Up and for viewport capture to finish. Hold-to-fly is the intentional exception to waiting for Up.
