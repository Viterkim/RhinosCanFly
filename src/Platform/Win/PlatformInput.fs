module RhinosCanFly.PlatformInput

open System
open System.Drawing
open RhinosCanFly.Platform.Win

[<Literal>]
let wheel_delta = Win32.WHEEL_DELTA

let wait_for_input () = Win32.wait_for_input Win32.INFINITE

let foreground_window () = Win32.GetForegroundWindow()

let right_mouse_button_down () =
    Win32.GetAsyncKeyState Win32.VK_RBUTTON < 0s

let root_window (window: nativeint) =
    let ancestor = Win32.GetAncestor(window, Win32.GA_ROOT)

    if ancestor = nativeint 0 then
        foreground_window ()
    else
        ancestor

let get_cursor_position () = Win32.get_cursor_position ()

let set_cursor_position (point: Point) = Win32.set_cursor_position point

let clear_mouse_hover (window: nativeint) = Win32.clear_mouse_hover window

let dismiss_native_tooltips (window: nativeint) = Win32.dismiss_native_tooltips window

let update_window (window: nativeint) = Win32.update_window window

let redraw_window (window: nativeint) = Win32.redraw_window window

let clip_cursor (rectangle: Rectangle) = Win32.clip_cursor rectangle

let clear_cursor_clip () = Win32.clear_cursor_clip ()

let focus (window: nativeint) = Win32.SetFocus window |> ignore

let hide_cursor () = Win32.ShowCursor false |> ignore

let show_cursor () = Win32.ShowCursor true |> ignore

type RawInputSession = RawInputThread.Session

let create_raw_input_wake () =
    let state = RawInputWake.create ()
    let clearPending = Action(fun () -> RawInputWake.clear state)

    Action(fun () -> RawInputWake.signal clearPending state)

let open_raw_input (config: FlyConfig) (input: InputAccumulator.State) (inputAvailable: Action) =
    RawInputThread.start config input inputAvailable

let close_raw_input (session: RawInputSession) = RawInputThread.stop session

let apply_mouse_button_overrides (config: FlyConfigFile) = MouseButtonOverrides.apply config

let mouse_button_right_click_enabled () =
    MouseButtonOverrides.right_click_enabled ()

let handle_view_manipulation_right_click (window: nativeint) =
    MouseButtonOverrides.handle_right_click window

let suspend_mouse_button_overrides () = MouseButtonOverrides.suspend ()

let resume_mouse_button_overrides () = MouseButtonOverrides.resume ()

let shutdown_mouse_button_overrides () = MouseButtonOverrides.shutdown ()
