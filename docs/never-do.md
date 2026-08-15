# Never do this

Don't use `Rhino.UI.MouseCallback` to take a mouse button and cancel its down/up events.

We did that for right click. Sometimes Rhino stopped painting after it: white dialogs, frozen panels and a frozen command line. Right clicking the command line made it wake up again. It had been there since the first version and only broke sometimes, so it took forever to find.

Right click now goes through one `WH_MOUSE` hook. It takes the whole down/up pair, stores what should happen, then the UI timer handles it. Do not call RhinoCommon or `RunScript` from inside the hook.

There is still one `MouseCallback`. Pivot and pan use `OnMouseMove` while they are active, and cancel the movement so Rhino doesn't move the view as well. It never handles button down/up.

Pivot and pan call `MouseRotateAroundTarget` and `MouseLateralDolly` directly. Do not bring fake middle mouse back.
