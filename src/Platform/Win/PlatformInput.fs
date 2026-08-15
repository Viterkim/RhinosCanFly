module RhinosCanFly.PlatformInput

open System
open Rhino.Display
open RhinosCanFly.Platform.Win

let wheel_delta = int64 Win32Native.WHEEL_DELTA

let foreground_root_window () =
    RootWindow(Win32Native.GetForegroundWindow())

let right_mouse_button_down () =
    Win32Native.GetAsyncKeyState Win32Native.VK_RBUTTON < 0s

let focus_view (view: RhinoView) =
    Win32Native.SetFocus view.Handle |> ignore

let wait_for_input () = Win32.wait_for_input ()

let root_window (view: RhinoView) =
    let ancestor = Win32Native.GetAncestor(view.Handle, Win32Native.GA_ROOT)

    RootWindow(if ancestor = nativeint 0 then view.Handle else ancestor)

let capture_viewport_host (view: RhinoView) =
    { document_serial_number = view.Document.RuntimeSerialNumber
      view_serial_number = view.RuntimeSerialNumber
      view_window = ViewWindowHandle view.Handle
      root_window = root_window view }

let viewport_matches_identity (identity: ViewportHostIdentity) (view: RhinoView) =
    let (ViewWindowHandle expectedHandle) = identity.view_window

    if Object.ReferenceEquals(view, null) then
        false
    else
        let document = view.Document

        not (Object.ReferenceEquals(document, null))
        && view.RuntimeSerialNumber = identity.view_serial_number
        && document.RuntimeSerialNumber = identity.document_serial_number
        && expectedHandle <> nativeint 0
        && Win32Native.IsWindow expectedHandle
        && view.Handle = expectedHandle

let viewport_host_exists (identity: ViewportHostIdentity) (view: RhinoView) =
    try
        viewport_matches_identity identity view
        && viewport_matches_identity identity (RhinoView.FromRuntimeSerialNumber identity.view_serial_number)
    with _ ->
        false

let viewport_host_is_active (identity: ViewportHostIdentity) (view: RhinoView) =
    try
        let (ViewWindowHandle expectedHandle) = identity.view_window

        if
            Object.ReferenceEquals(view, null)
            || expectedHandle = nativeint 0
            || view.RuntimeSerialNumber <> identity.view_serial_number
            || view.Handle <> expectedHandle
            || not (Win32Native.IsWindow expectedHandle)
        then
            false
        else
            let activeDocument = Rhino.RhinoDoc.ActiveDoc

            if
                Object.ReferenceEquals(activeDocument, null)
                || activeDocument.RuntimeSerialNumber <> identity.document_serial_number
            then
                false
            else
                let activeView = activeDocument.Views.ActiveView

                not (Object.ReferenceEquals(activeView, null))
                && activeView.RuntimeSerialNumber = identity.view_serial_number
                && activeView.Handle = expectedHandle
    with _ ->
        false

let viewport_host_is_foreground (identity: ViewportHostIdentity) (view: RhinoView) =
    viewport_host_is_active identity view
    && foreground_root_window () = identity.root_window

let get_cursor_position () =
    Win32.get_cursor_position () |> Result.map CursorPosition

let restore_cursor_position_if_foreground (window: RootWindow) (position: CursorPosition) =
    let (RootWindow root) = window

    if Win32Native.IsWindow root && Win32Native.GetForegroundWindow() = root then
        let (CursorPosition point) = position
        Win32.set_cursor_position point
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

let request_application_redraw () =
    try
        Win32.request_application_redraw (Rhino.RhinoApp.MainWindowHandle())
    with _ ->
        ()

let acquire_cursor_clip (view: RhinoView) =
    Win32.acquire_cursor_clip view.ScreenRectangle

let release_cursor_clip (lease: CursorClipLease) = Win32.release_cursor_clip lease

let retry_cursor_clip_cleanup () = Win32.retry_cursor_clip_cleanup ()

let cursor_clip_recovery_count () = Win32.cursor_clip_recovery_count ()

let root_window_valid (rootWindow: RootWindow) =
    let (RootWindow window) = rootWindow
    Win32Native.IsWindow window

let hide_cursor () = Win32Native.ShowCursor false |> ignore

let show_cursor () = Win32Native.ShowCursor true |> ignore

type RawInputSession = RawInputThread.Session
type RawInputWake = RhinosCanFly.Platform.Win.RawInputWake.State

let create_raw_input_wake (window: RootWindow) =
    RhinosCanFly.Platform.Win.RawInputWake.create window

let raw_input_wake_action (wake: RawInputWake) =
    Action(fun () -> RhinosCanFly.Platform.Win.RawInputWake.signal wake)

let acknowledge_raw_input_wake (wake: RawInputWake) =
    RhinosCanFly.Platform.Win.RawInputWake.acknowledge wake

let wake_flight_loop (wake: RawInputWake) =
    RhinosCanFly.Platform.Win.RawInputWake.signal wake

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
    FlightKeyboardSuppression.start bindings

let release_flight_keyboard () = FlightKeyboardSuppression.stop ()

let shutdown_flight_keyboard () = FlightKeyboardSuppression.shutdown ()

let flight_binding_down (binding: KeyBinding) =
    FlightKeyboardSuppression.binding_is_down binding

let apply_mouse_button_overrides (config: MouseOverrideConfig) = MouseButtonOverrides.apply config

let start_pivot (view: RhinoView) (completion: Action option) =
    MouseButtonOverrides.start_view_latch view ViewNavigationTypes.ViewNavigationMode.Pivot completion

let stop_pivot () =
    MouseButtonOverrides.stop_view_latch ViewNavigationTypes.ViewNavigationMode.Pivot

let start_pan (view: RhinoView) (completion: Action option) =
    MouseButtonOverrides.start_view_latch view ViewNavigationTypes.ViewNavigationMode.Pan completion

let stop_pan () =
    MouseButtonOverrides.stop_view_latch ViewNavigationTypes.ViewNavigationMode.Pan

let pivot_active () =
    MouseButtonOverrides.view_latch_is ViewNavigationTypes.ViewNavigationMode.Pivot

let pan_active () =
    MouseButtonOverrides.view_latch_is ViewNavigationTypes.ViewNavigationMode.Pan

let suspend_mouse_button_overrides () = MouseButtonOverrides.suspend ()

let resume_mouse_button_overrides (lease: InputSuspensionLease) = MouseButtonOverrides.resume lease

let shutdown_mouse_button_overrides () = MouseButtonOverrides.shutdown ()

let retry_input_hook_cleanup () =
    MouseButtonOverrides.retry_hook_cleanup ()
