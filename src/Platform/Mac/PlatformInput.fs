module RhinosCanFly.PlatformInput

open System
open Rhino
open Rhino.Display
open Eto.Drawing
open RhinosCanFly.Platform.Mac

// The AppKit monitor already reports application/window deactivation.
let watch_entry_focus (_host: ViewportHostIdentity) (_invalidated: bool ref) : IDisposable option = None

let fallback_window (view: RhinoView) =
    let native = MacNative.load ()
    let window = native.window_from_handle.Invoke view.Handle

    if window <> 0n || view.Floating then
        window
    else
        let parent = Rhino.UI.RhinoEtoApp.MainWindowForDocument view.Document

        if isNull parent then
            0n
        else
            native.window_from_handle.Invoke parent.NativeHandle

let capture_viewport_host (view: RhinoView) : ViewportHostIdentity =
    if isNull view || isNull view.Document then
        invalidOp "The viewport has no document."

    let native = MacNative.load ()
    let sdk_window = native.view_window.Invoke view.RuntimeSerialNumber

    let window =
        if sdk_window <> 0n then
            sdk_window
        else
            fallback_window view

    if window = 0n then
        invalidOp "Rhino's Mac viewport window could not be resolved from its SDK exports or native handle."

    { document_serial_number = view.Document.RuntimeSerialNumber
      view_serial_number = view.RuntimeSerialNumber
      viewport_id = view.ActiveViewportID
      view = view
      view_window = view.Handle
      floating = view.Floating
      sdk_window = sdk_window <> 0n
      window = window }

let viewport_window_matches (identity: ViewportHostIdentity) (view: RhinoView) =
    identity.window <> 0n
    && view.Handle = identity.view_window
    && view.Floating = identity.floating
    && (if identity.sdk_window then
            MacNative.view_window identity.view_serial_number = identity.window
        else
            fallback_window view = identity.window)

let viewport_host_is_active (identity: ViewportHostIdentity) (view: RhinoView) =
    try
        let document = RhinoDoc.ActiveDoc
        let active_view = if isNull document then null else document.Views.ActiveView

        not (isNull view)
        && not (isNull view.Document)
        && not (isNull document)
        && document.RuntimeSerialNumber = identity.document_serial_number
        && view.Document.RuntimeSerialNumber = identity.document_serial_number
        && view.RuntimeSerialNumber = identity.view_serial_number
        && view.ActiveViewportID = identity.viewport_id
        && viewport_window_matches identity view
        && not (isNull active_view)
        && active_view.RuntimeSerialNumber = identity.view_serial_number
    with _ ->
        false

let viewport_application_is_foreground (identity: ViewportHostIdentity) =
    identity.window <> 0n && MacNative.foreground_window () = identity.window

let viewport_host_exists (identity: ViewportHostIdentity) (view: RhinoView) =
    try
        not (isNull view)
        && not (isNull view.Document)
        && view.Document.RuntimeSerialNumber = identity.document_serial_number
        && view.RuntimeSerialNumber = identity.view_serial_number
        && viewport_window_matches identity view
        && (view.MainViewport.Id = identity.viewport_id
            || match view with
               | :? RhinoPageView as page ->
                   let details = page.GetDetailViews()

                   not (isNull details)
                   && Array.exists
                       (fun (detail: Rhino.DocObjects.DetailViewObject) ->
                           not detail.IsDeleted && detail.Viewport.Id = identity.viewport_id)
                       details
               | _ -> false)
    with _ ->
        false

let viewport_host_windows_exist (identity: ViewportHostIdentity) =
    try
        not (isNull identity.view)
        && identity.view.ActiveViewportID = identity.viewport_id
        && not (isNull identity.view.Document)
        && identity.view.Document.RuntimeSerialNumber = identity.document_serial_number
        && viewport_window_matches identity identity.view
    with _ ->
        false

let viewport_id_matches (identity: ViewportHostIdentity) (view: RhinoView) =
    not (isNull view) && view.ActiveViewportID = identity.viewport_id

let viewport_host_is_foreground (identity: ViewportHostIdentity) (view: RhinoView) =
    viewport_application_is_foreground identity
    && viewport_host_is_active identity view

let get_cursor_position () =
    try
        let point = MacNative.cursor_position ()
        Ok(CursorPosition(PointF(single point.x, single point.y)))
    with error ->
        Error error.Message

let right_mouse_button_down () = MacNative.mouse_down 1
let middle_mouse_button_down () = MacNative.mouse_down 2
let mouse4_button_down () = MacNative.mouse_down 3
let mouse5_button_down () = MacNative.mouse_down 4

let wheel_delta = 120L
let wheel_zoom_steps (delta: int64) = float delta / float wheel_delta

let update_window (_view: RhinoView) = ()

let request_application_redraw () =
    let document = RhinoDoc.ActiveDoc

    if not (isNull document) then
        document.Views.Redraw()
