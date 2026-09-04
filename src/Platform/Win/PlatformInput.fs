module RhinosCanFly.PlatformInput

open System
open Rhino.Display
open RhinosCanFly.Platform.Win

let wheel_delta = int64 Win32Native.WHEEL_DELTA

let wheel_zoom_steps_per_delta =
    let scroll_lines = System.Windows.Forms.SystemInformation.MouseWheelScrollLines
    let line_count = if scroll_lines > 0 then scroll_lines else 1
    float line_count / float Win32Native.WHEEL_DELTA

let wheel_zoom_steps (delta: int64) =
    float delta * wheel_zoom_steps_per_delta

let foreground_root_window () =
    RootWindow(Win32Native.GetForegroundWindow())

let right_mouse_button_down () =
    Win32Native.GetAsyncKeyState Win32Native.VK_RBUTTON < 0s

let middle_mouse_button_down () =
    Win32Native.GetAsyncKeyState Win32Native.VK_MBUTTON < 0s

let mouse4_button_down () =
    Win32Native.GetAsyncKeyState Win32Native.VK_XBUTTON1 < 0s

let mouse5_button_down () =
    Win32Native.GetAsyncKeyState Win32Native.VK_XBUTTON2 < 0s

let focus_view (view: RhinoView) =
    Win32Native.SetFocus view.Handle |> ignore

let wait_for_input_for (timeout_milliseconds: int) =
    Win32.wait_for_input_for timeout_milliseconds

let root_window (view: RhinoView) =
    let ancestor = Win32Native.GetAncestor(view.Handle, Win32Native.GA_ROOT)

    RootWindow(if ancestor = nativeint 0 then view.Handle else ancestor)

let capture_viewport_host (view: RhinoView) =
    { document_serial_number = view.Document.RuntimeSerialNumber
      view_serial_number = view.RuntimeSerialNumber
      viewport_id = view.ActiveViewportID
      view_window = ViewWindowHandle view.Handle
      root_window = root_window view }

let viewport_matches_identity (identity: ViewportHostIdentity) (view: RhinoView) =
    let (ViewWindowHandle expected_handle) = identity.view_window

    if Object.ReferenceEquals(view, null) then
        false
    else
        let document = view.Document

        not (Object.ReferenceEquals(document, null))
        && view.RuntimeSerialNumber = identity.view_serial_number
        && document.RuntimeSerialNumber = identity.document_serial_number
        && expected_handle <> nativeint 0
        && Win32Native.IsWindow expected_handle
        && view.Handle = expected_handle

let viewport_host_exists (identity: ViewportHostIdentity) (view: RhinoView) =
    try
        viewport_matches_identity identity view
        && viewport_matches_identity identity (RhinoView.FromRuntimeSerialNumber identity.view_serial_number)
    with _ ->
        false

let viewport_host_is_active (identity: ViewportHostIdentity) (view: RhinoView) =
    try
        let (ViewWindowHandle expected_handle) = identity.view_window

        if
            Object.ReferenceEquals(view, null)
            || expected_handle = nativeint 0
            || view.RuntimeSerialNumber <> identity.view_serial_number
            || view.Handle <> expected_handle
            || not (Win32Native.IsWindow expected_handle)
        then
            false
        else
            let active_document = Rhino.RhinoDoc.ActiveDoc

            if
                Object.ReferenceEquals(active_document, null)
                || active_document.RuntimeSerialNumber <> identity.document_serial_number
            then
                false
            else
                let active_view = active_document.Views.ActiveView

                not (Object.ReferenceEquals(active_view, null))
                && active_view.RuntimeSerialNumber = identity.view_serial_number
                && active_view.Handle = expected_handle
                && active_view.ActiveViewportID = identity.viewport_id
    with _ ->
        false

let viewport_id_matches (identity: ViewportHostIdentity) (view: RhinoView) =
    try
        not (Object.ReferenceEquals(view, null))
        && view.ActiveViewportID = identity.viewport_id
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
        let mutable native_point = Unchecked.defaultof<Win32Native.NativePoint>
        native_point.x <- point.X
        native_point.y <- point.Y
        let window = Win32Native.WindowFromPoint native_point

        Ok(
            Win32Native.IsWindow view.Handle
            && Win32Native.IsWindowEnabled view.Handle
            && (window = view.Handle || Win32Native.IsChild(view.Handle, window))
        )
    | Error error -> Error error

let clear_mouse_hover (view: RhinoView) = Win32.clear_mouse_hover view.Handle

let dismiss_native_tooltips (root_window: RootWindow) =
    let (RootWindow window) = root_window
    Win32.dismiss_native_tooltips window

let update_window (view: RhinoView) = Win32.update_window view.Handle

let request_application_redraw () =
    try
        Win32.request_application_redraw (Rhino.RhinoApp.MainWindowHandle())
    with _ ->
        ()

let viewport_host_windows_exist (identity: ViewportHostIdentity) =
    let (RootWindow root_window) = identity.root_window
    let (ViewWindowHandle view_window) = identity.view_window

    root_window <> nativeint 0
    && view_window <> nativeint 0
    && Win32Native.IsWindow root_window
    && Win32Native.IsWindow view_window

let hide_cursor () = Win32Native.ShowCursor false |> ignore

let show_cursor () = Win32Native.ShowCursor true |> ignore
