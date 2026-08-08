module RhinosCanFly.FlightRedraw

open Rhino.Display

let redraw (mode: ViewportRedrawMode) (view: RhinoView) =
    match mode with
    | ViewportRedrawMode.Rhino -> view.Redraw()
    | ViewportRedrawMode.RhinoImmediate ->
        view.Redraw()

        match PlatformInput.update_window view.Handle with
        | Ok() -> ()
        | Error error -> failwith error
    | ViewportRedrawMode.NativeWindow ->
        match PlatformInput.redraw_window view.Handle with
        | Ok() -> ()
        | Error error -> failwith error
