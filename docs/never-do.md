# Never do this

Don't use `Rhino.UI.MouseCallback` to own and cancel a mouse button down/up pair.

The old right click entry did this. It could leave Rhino with white dialogs, frozen panels and a dead command line.

Right click now uses `WH_MOUSE`. The hook owns the whole down/up pair and only stores what happened. The timer does the Rhino work later.

Never call RhinoCommon or `RunScript` inside the hook.

Pivot and pan still use `MouseCallback` for `OnMouseMove` only. Never for button down/up.

Pivot and pan call `MouseRotateAroundTarget` and `MouseLateralDolly` directly. Do not bring fake middle mouse back.
