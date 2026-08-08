module RhinosCanFly.FlightRedraw

open Rhino.Display

type Mode =
    | RhinoView
    | RhinoViewImmediate
    | NativeWindow

let mutable mode = RhinoView

let name () =
    match mode with
    | RhinoView -> "RhinoView.Redraw"
    | RhinoViewImmediate -> "RhinoView.Redraw + UpdateWindow"
    | NativeWindow -> "InvalidateRect + UpdateWindow"

let toggle () =
    mode <-
        match mode with
        | RhinoView -> RhinoViewImmediate
        | RhinoViewImmediate -> NativeWindow
        | NativeWindow -> RhinoView

    name ()

let redraw (view: RhinoView) =
    match mode with
    | RhinoView -> view.Redraw()
    | RhinoViewImmediate ->
        view.Redraw()

        match PlatformInput.update_window view.Handle with
        | Ok() -> ()
        | Error error -> failwith error
    | NativeWindow ->
        match PlatformInput.redraw_window view.Handle with
        | Ok() -> ()
        | Error error -> failwith error
