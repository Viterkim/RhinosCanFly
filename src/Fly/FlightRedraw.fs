module RhinosCanFly.FlightRedraw

open Rhino.Display

let redraw (mode: ViewportRedrawMode) (view: RhinoView) =
    match mode with
    | ViewportRedrawMode.Rhino -> view.Redraw()
    | ViewportRedrawMode.RhinoImmediate ->
        view.Redraw()
        PlatformInput.update_window view
    | ViewportRedrawMode.NativeWindow -> PlatformInput.redraw_window view
    | _ -> view.Redraw()
