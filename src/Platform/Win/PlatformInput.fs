module RhinosCanFly.PlatformInput

open System
open System.Drawing
open RhinosCanFly.Platform.Win

[<Literal>]
let wheel_delta = Win32.WHEEL_DELTA

let wait_for_input () = Win32.wait_for_input Win32.INFINITE

let foreground_window () = Win32.GetForegroundWindow()

let root_window (window: nativeint) =
    let ancestor = Win32.GetAncestor(window, Win32.GA_ROOT)

    if ancestor = nativeint 0 then
        foreground_window ()
    else
        ancestor

let get_cursor_position () = Win32.get_cursor_position ()

let set_cursor_position (point: Point) = Win32.set_cursor_position point

let clear_mouse_hover (window: nativeint) = Win32.clear_mouse_hover window

let clip_cursor (rectangle: Rectangle) = Win32.clip_cursor rectangle

let clear_cursor_clip () = Win32.clear_cursor_clip ()

let focus (window: nativeint) = Win32.SetFocus window |> ignore

let hide_cursor () = Win32.ShowCursor false |> ignore

let show_cursor () = Win32.ShowCursor true |> ignore

let open_raw_input (window: nativeint) (state: FlyState) : IDisposable = new RawInputWindow(window, state)

let apply_mouse_button_overrides (config: FlyConfigFile) = MouseButtonOverrides.apply config

let suspend_mouse_button_overrides () = MouseButtonOverrides.suspend ()

let resume_mouse_button_overrides () = MouseButtonOverrides.resume ()

let shutdown_mouse_button_overrides () = MouseButtonOverrides.shutdown ()
