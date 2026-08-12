module RhinosCanFly.FlightRedraw

open System.Diagnostics
open Rhino.Display

let redraw (mode: ViewportRedrawMode) (view: RhinoView) =
    let startedAt = Stopwatch.GetTimestamp()

    try
        match mode with
        | ViewportRedrawMode.Rhino -> view.Redraw()
        | ViewportRedrawMode.RhinoImmediate ->
            view.Redraw()
            PlatformInput.update_window view
        | ViewportRedrawMode.NativeWindow -> PlatformInput.redraw_window view
        | _ -> view.Redraw()
    finally
        PlatformInput.record_redraw (Stopwatch.GetTimestamp() - startedAt)
