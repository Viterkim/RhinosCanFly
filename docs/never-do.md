# Never do this

Don't use `Rhino.UI.MouseCallback` to own and cancel a mouse button down/up pair.

The old right click entry did this. It could leave Rhino with white dialogs, frozen panels and a dead command line.

Right click now uses `WH_MOUSE`. The hook owns the whole down/up pair and only stores what happened. The timer does the Rhino work later.

Never call RhinoCommon or `RunScript` inside the hook.

Pivot, pan and parallel zoom use the same raw-input worker as flying. Rhino still performs the viewport operations through `MouseRotateAroundTarget`, `MouseLateralDolly` and `Magnify`. Do not bring `MouseCallback` or fake middle mouse back.

Standalone navigation drains on `RhinoApp.MainLoop`. That event runs on every Rhino message-loop iteration, and the raw worker posts `WM_NULL` to wake it when input arrives. It is not the idle event. Do not replace it with a timer or a blocking flight loop.
