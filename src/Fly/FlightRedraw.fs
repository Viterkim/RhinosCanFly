module RhinosCanFly.FlightRedraw

open Rhino.Display

let redraw (mode: ViewportPaintMode) (view: RhinoView) =
    view.Redraw()

    if mode <> ViewportPaintMode.Queued then
        PlatformInput.update_window view
