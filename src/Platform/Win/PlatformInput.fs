module RhinosCanFly.PlatformInput

open System
open Rhino.Display
open RhinosCanFly.Platform.Win

let wheel_delta = int64 Win32Native.WHEEL_DELTA

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

let wait_for_input_for (timeoutMilliseconds: int) =
    Win32.wait_for_input_for timeoutMilliseconds

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
                && activeView.ActiveViewportID = identity.viewport_id
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

let request_application_redraw () =
    try
        Win32.request_application_redraw (Rhino.RhinoApp.MainWindowHandle())
    with _ ->
        ()

let viewport_host_windows_exist (identity: ViewportHostIdentity) =
    let (RootWindow rootWindow) = identity.root_window
    let (ViewWindowHandle viewWindow) = identity.view_window

    rootWindow <> nativeint 0
    && viewWindow <> nativeint 0
    && Win32Native.IsWindow rootWindow
    && Win32Native.IsWindow viewWindow

let hide_cursor () = Win32Native.ShowCursor false |> ignore

let show_cursor () = Win32Native.ShowCursor true |> ignore
