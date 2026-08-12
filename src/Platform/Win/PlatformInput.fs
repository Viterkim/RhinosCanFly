module RhinosCanFly.PlatformInput

open System
open Rhino.Display
open RhinosCanFly.Platform.Win

let wheel_delta = int64 Win32Native.WHEEL_DELTA

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

let restore_cursor_position_if_foreground (window: RootWindow) (position: CursorPosition) =
    let (RootWindow root) = window

    if Win32Native.IsWindow root && Win32Native.GetForegroundWindow() = root then
        restore_cursor_position position
    else
        Ok()

let cursor_is_over_view (view: RhinoView) =
    match get_cursor_position () with
    | Ok(CursorPosition point) ->
        let mutable nativePoint = Unchecked.defaultof<Win32Native.NativePoint>
        nativePoint.x <- point.X
        nativePoint.y <- point.Y
        let window = Win32Native.WindowFromPoint nativePoint

        Ok(
            Win32Native.IsWindow view.Handle
            && Win32Native.IsWindowEnabled view.Handle
            && (window = view.Handle || Win32Native.IsChild(view.Handle, window))
        )
    | Error error -> Error error

let clear_mouse_hover (view: RhinoView) = Win32.clear_mouse_hover view.Handle

let dismiss_native_tooltips (rootWindow: RootWindow) =
    let (RootWindow window) = rootWindow
    Win32.dismiss_native_tooltips window

let update_window (view: RhinoView) = Win32.update_window view.Handle

let redraw_window (view: RhinoView) = Win32.redraw_window view.Handle

let acquire_cursor_clip (view: RhinoView) =
    Win32.acquire_cursor_clip view.ScreenRectangle

let release_cursor_clip (lease: CursorClipLease) = Win32.release_cursor_clip lease

let root_window_valid (rootWindow: RootWindow) =
    let (RootWindow window) = rootWindow
    Win32Native.IsWindow window

let hide_cursor () = Win32Native.ShowCursor false |> ignore

let show_cursor () = Win32Native.ShowCursor true |> ignore

type RawInputSession = RawInputThread.Session
type RawInputWake = RhinosCanFly.Platform.Win.RawInputWake.State

let create_raw_input_wake () =
    RhinosCanFly.Platform.Win.RawInputWake.create ()

let raw_input_wake_action (wake: RawInputWake) =
    Action(fun () -> RhinosCanFly.Platform.Win.RawInputWake.signal wake)

let clear_raw_input_wake (wake: RawInputWake) =
    RhinosCanFly.Platform.Win.RawInputWake.clear wake

let wait_for_raw_input (wake: RawInputWake) (timeout: TimeSpan) =
    let milliseconds =
        timeout.TotalMilliseconds
        |> max 0.0
        |> min (float (UInt32.MaxValue - 1u))
        |> uint32

    RhinosCanFly.Platform.Win.RawInputWake.wait wake milliseconds

let dispose_raw_input_wake (wake: RawInputWake) =
    RhinosCanFly.Platform.Win.RawInputWake.dispose wake

let open_raw_input
    (config: RawInputConfig)
    (sessionMode: FlightSessionMode)
    (input: InputAccumulator.State)
    (inputAvailable: Action)
    =
    RawInputThread.start config sessionMode input inputAvailable

let raw_input_start_requires_restart (error: exn) =
    match error with
    | :? RawInputThread.StartFailureException as failure -> failure.RestartRequired
    | _ -> false

let request_raw_input_stop (session: RawInputSession) = RawInputThread.request_stop session

let close_raw_input (session: RawInputSession) = RawInputThread.stop session

let raw_input_runtime_failed (session: RawInputSession) = RawInputThread.runtime_failed session

let retry_raw_input_cleanup () = RawInputThread.retry_recovery ()

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

let record_input_suspended () =
    InputDiagnostics.record InputDiagnostics.EventKind.InputSuspended 0L 0L

let record_input_resumed () =
    InputDiagnostics.record InputDiagnostics.EventKind.InputResumed 0L 0L

let record_view_capture_timed_out () =
    InputDiagnostics.record InputDiagnostics.EventKind.ViewCaptureTimedOut 0L 0L

let input_diagnostic_lines () = InputDiagnostics.lines ()

let retry_input_hook_cleanup () =
    MouseButtonOverrides.retry_hook_cleanup ()

let input_state_diagnostic_lines () =
    MouseButtonOverrides.diagnostic_lines ()
