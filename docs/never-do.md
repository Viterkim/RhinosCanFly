# Never do this

Don't use `Rhino.UI.MouseCallback` to own and cancel a mouse button down/up pair.

The old right click entry did this. It could leave Rhino with white dialogs, frozen panels and a dead command line.

Right click now uses `WH_MOUSE`. The hook owns the button pair and queues the work for Rhino's UI loop.

Never call RhinoCommon or `RunScript` inside the hook.
