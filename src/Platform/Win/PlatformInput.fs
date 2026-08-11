module RhinosCanFly.PlatformInput

open System
open Rhino.Display
open RhinosCanFly.Platform.Win

[<Literal>]
let wheel_delta = Win32Native.WHEEL_DELTA

let wait_for_input () =
    Win32.wait_for_input Win32Native.INFINITE

let wait_for_input_for (timeout: TimeSpan) =
    let milliseconds =
        timeout.TotalMilliseconds
        |> max 0.0
        |> min (float (UInt32.MaxValue - 1u))
        |> uint32

    Win32.wait_for_input milliseconds

let foreground_root_window () =
    RootWindow(Win32Native.GetForegroundWindow())

let right_mouse_button_down () =
    Win32Native.GetAsyncKeyState Win32Native.VK_RBUTTON < 0s

let root_window (view: RhinoView) =
    let ancestor = Win32Native.GetAncestor(view.Handle, Win32Native.GA_ROOT)

    if ancestor = nativeint 0 then
        foreground_root_window ()
    else
        RootWindow ancestor

let get_cursor_position () =
    Win32.get_cursor_position () |> Result.map CursorPosition

let restore_cursor_position (position: CursorPosition) =
    let (CursorPosition point) = position
    Win32.set_cursor_position point

let cursor_is_over_view (view: RhinoView) =
    match get_cursor_position () with
    | Ok(CursorPosition point) -> Ok(view.ScreenRectangle.Contains point)
    | Error error -> Error error

let clear_mouse_hover (view: RhinoView) = Win32.clear_mouse_hover view.Handle

let dismiss_native_tooltips (rootWindow: RootWindow) =
    let (RootWindow window) = rootWindow
    Win32.dismiss_native_tooltips window

let update_window (view: RhinoView) = Win32.update_window view.Handle

let redraw_window (view: RhinoView) = Win32.redraw_window view.Handle

let clip_cursor (view: RhinoView) = Win32.clip_cursor view.ScreenRectangle

let clear_cursor_clip () = Win32.clear_cursor_clip ()

let focus (view: RhinoView) =
    Win32Native.SetFocus view.Handle |> ignore

let hide_cursor () = Win32Native.ShowCursor false |> ignore

let show_cursor () = Win32Native.ShowCursor true |> ignore

type RawInputSession = RawInputThread.Session
type RawInputWake = RhinosCanFly.Platform.Win.RawInputWake.State

let create_raw_input_wake (window: RootWindow) =
    RhinosCanFly.Platform.Win.RawInputWake.create window

let raw_input_wake_action (wake: RawInputWake) =
    Action(fun () -> RhinosCanFly.Platform.Win.RawInputWake.signal wake)

let clear_raw_input_wake (wake: RawInputWake) =
    RhinosCanFly.Platform.Win.RawInputWake.clear wake

let open_raw_input
    (config: RawInputConfig)
    (sessionMode: FlightSessionMode)
    (input: InputAccumulator.State)
    (inputAvailable: Action)
    =
    RawInputThread.start config sessionMode input inputAvailable

let close_raw_input (session: RawInputSession) = RawInputThread.stop session

let suppress_flight_keyboard (bindings: FlightBindings) =
    MouseButtonOverrides.suppress_flight_keyboard bindings

let release_flight_keyboard () =
    MouseButtonOverrides.release_flight_keyboard ()

let apply_mouse_button_overrides (config: MouseOverrideConfig) = MouseButtonOverrides.apply config

let mouse_button_right_click_enabled () =
    MouseButtonOverrides.right_click_enabled ()

let handle_view_manipulation_right_click (view: RhinoView) =
    MouseButtonOverrides.handle_right_click (ViewNavigationState.root_window view.Handle)

let start_pivot (view: RhinoView) (completion: Action option) =
    MouseButtonOverrides.start_view_latch
        (ViewNavigationState.root_window view.Handle)
        ViewNavigationTypes.ViewLatchMode.Pivot
        completion

let stop_pivot () =
    MouseButtonOverrides.stop_view_latch ViewNavigationTypes.ViewLatchMode.Pivot

let start_pan (view: RhinoView) (completion: Action option) =
    MouseButtonOverrides.start_view_latch
        (ViewNavigationState.root_window view.Handle)
        ViewNavigationTypes.ViewLatchMode.Pan
        completion

let stop_pan () =
    MouseButtonOverrides.stop_view_latch ViewNavigationTypes.ViewLatchMode.Pan

let pivot_active () =
    MouseButtonOverrides.view_latch_is ViewNavigationTypes.ViewLatchMode.Pivot

let pan_active () =
    MouseButtonOverrides.view_latch_is ViewNavigationTypes.ViewLatchMode.Pan

let suspend_mouse_button_overrides () = MouseButtonOverrides.suspend ()

let resume_mouse_button_overrides () = MouseButtonOverrides.resume ()

let shutdown_mouse_button_overrides () = MouseButtonOverrides.shutdown ()
