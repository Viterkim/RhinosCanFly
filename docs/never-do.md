# Never do this

Do not use `Rhino.UI.MouseCallback` with `event.Cancel <- true` to steal right click.

Our old right-click entry cancelled the down and up there, then started flying from an Idle/MainLoop callback. Sometimes Rhino/Eto stopped painting: white dialogs, frozen command line and frozen panels. Right clicking the command line made it wake up again.

It was in the plugin from the first version. Testing finally narrowed it down by running the full plugin without that callback, then putting right-click entry back through the Windows mouse hook. That version takes the complete down/up pair before Rhino sees either one and has not triggered the bug so far.

Using `MouseCallback` to observe something is fine. NEVER use it to hijack RMB and cancel Rhino's handling. The old stuff looks simpler but breaks.

Do it below Rhino instead. Use one thread `WH_MOUSE` hook, check cached viewport window handles, take the whole down/up pair, and store one typed action. Handle it on the normal UI timer afterward. Do not call RhinoCommon or `RunScript` from inside the hook, and do not add an Idle/MainLoop handler for every click.

Pivot and pan are direct viewport calls now: `MouseRotateAroundTarget` and `MouseLateralDolly`. `RhinosCanFlyPivot`, `RhinosCanFlyPan`, Shift/Alt + RMB and Mouse4/5 all enter the same navigation state. Do not bring fake middle mouse back.
