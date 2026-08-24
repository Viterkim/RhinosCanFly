module RhinosCanFly.Platform.Win.GestureNavigationTransitions

open System.Diagnostics
open System.Drawing
open Rhino.Display
open RhinosCanFly
open RhinosCanFly.Platform.Win.ViewNavigationTypes

let complete_view_latch (state: State) =
    let previous = state.view_latch
    state.view_latch <- NoViewLatch
    ViewNavigationState.complete_view_latch previous

let uses_cursor_outside_flight (state: State) (owner: GestureOwner) =
    match owner with
    | GestureOwner.ModifiedRightClick -> true
    | GestureOwner.Middle -> state.routing.outside_flight_cursor.middle
    | GestureOwner.Mouse4 -> state.routing.outside_flight_cursor.mouse4
    | GestureOwner.Mouse5 -> state.routing.outside_flight_cursor.mouse5

let client_target_point (state: State) (owner: GestureOwner) (view: RhinoView) (screenPoint: Point) =
    if uses_cursor_outside_flight state owner then
        let point = view.ActiveViewport.ScreenToClient screenPoint
        { x = point.X; y = point.Y }
    else
        let bounds = view.ActiveViewport.Bounds

        { x = bounds.Width / 2
          y = bounds.Height / 2 }

let stop (state: State) =
    state.gesture_navigation <- NoGestureNavigation
    ViewNavigationState.stop_timer_if_idle state

let restore_original_target (host: ViewportHostIdentity) (originalTarget: Rhino.Geometry.Point3d voption) =
    match originalTarget with
    | ValueNone -> Ok()
    | ValueSome target ->
        try
            let view = RhinoView.FromRuntimeSerialNumber host.view_serial_number

            if
                not (isNull view)
                && not (isNull view.Document)
                && view.Document.RuntimeSerialNumber = host.document_serial_number
                && view.ActiveViewportID = host.viewport_id
            then
                view.ActiveViewport.SetCameraTarget(target, false)
                view.Redraw()

            Ok()
        with error ->
            Error $"Could not restore the navigation target: {error.Message}"

let rollback_start (state: State) =
    let session =
        match state.gesture_navigation with
        | GestureNavigationActive active -> ValueSome active
        | NoGestureNavigation -> ValueNone

    stop state

    match session with
    | ValueSome active -> restore_original_target active.host active.original_target
    | ValueNone -> Ok()

let begin_navigation
    (state: State)
    (owner: GestureOwner)
    (host: ViewportHostIdentity)
    (screenPoint: Point)
    (mode: ViewNavigationMode)
    (lifetime: GestureLifetime)
    =
    let mutable canStart = true

    match state.gesture_navigation with
    | GestureNavigationActive current when
        current.owner = owner
        && current.mode = mode
        && current.lifetime = GestureLifetime.Toggle
        ->
        stop state
        canStart <- false
    | GestureNavigationActive _ -> stop state
    | NoGestureNavigation -> ()

    if not canStart then
        Ok()
    else
        match complete_view_latch state with
        | Error error -> Error error
        | Ok() ->
            let view = RhinoView.FromRuntimeSerialNumber host.view_serial_number

            let originalTarget =
                if isNull view || isNull view.Document then
                    ValueNone
                else
                    ValueSome view.ActiveViewport.CameraTarget

            let targetPoint =
                if
                    isNull view
                    || isNull view.Document
                    || not (uses_cursor_outside_flight state owner)
                then
                    NavigationTargetPoint.ViewCenter
                else
                    NavigationTargetPoint.ClientPoint(client_target_point state owner view screenPoint)

            match state.routing.prepare_navigation host targetPoint mode with
            | Error error ->
                match restore_original_target host originalTarget with
                | Ok() -> Error error
                | Error restoreError -> Error $"{error}; {restoreError}"
            | Ok prepared ->
                let preparedView = RhinoView.FromRuntimeSerialNumber prepared.view_serial_number

                if isNull preparedView || isNull preparedView.Document then
                    match restore_original_target host originalTarget with
                    | Ok() -> Error "The navigation viewport disappeared during startup."
                    | Error restoreError -> Error $"The navigation viewport disappeared during startup; {restoreError}"
                else
                    state.gesture_navigation <-
                        GestureNavigationActive
                            { owner = owner
                              host = prepared
                              mode = mode
                              lifetime = lifetime
                              pivot_center = preparedView.ActiveViewport.CameraTarget
                              original_target = originalTarget }

                    ViewNavigationState.keep_timer_running state
                    Ok()

let press
    (state: State)
    (owner: GestureOwner)
    (action: RoutedMouseAction)
    (host: ViewportHostIdentity)
    (screenPoint: Point)
    =
    match action with
    | RoutedMouseAction.Off -> Ok()
    | RoutedMouseAction.Retarget mode ->
        let view = RhinoView.FromRuntimeSerialNumber host.view_serial_number

        if isNull view then
            Error "The retarget viewport is unavailable."
        else
            state.routing.retarget host (client_target_point state owner view screenPoint) mode
    | RoutedMouseAction.TogglePivot ->
        begin_navigation state owner host screenPoint ViewNavigationMode.Pivot GestureLifetime.Toggle
    | RoutedMouseAction.HoldPivot ->
        begin_navigation state owner host screenPoint ViewNavigationMode.Pivot GestureLifetime.Hold
    | RoutedMouseAction.TogglePan ->
        begin_navigation state owner host screenPoint ViewNavigationMode.Pan GestureLifetime.Toggle
    | RoutedMouseAction.HoldPan ->
        begin_navigation state owner host screenPoint ViewNavigationMode.Pan GestureLifetime.Hold

let release (state: State) (owner: GestureOwner) =
    match state.gesture_navigation with
    | GestureNavigationActive current when current.owner = owner && current.lifetime = GestureLifetime.Hold ->
        stop state
    | GestureNavigationActive _
    | NoGestureNavigation -> ()

let owner_button_down (owner: GestureOwner) =
    match owner with
    | GestureOwner.ModifiedRightClick -> Win32Native.GetAsyncKeyState Win32Native.VK_RBUTTON < 0s
    | GestureOwner.Middle -> Win32Native.GetAsyncKeyState Win32Native.VK_MBUTTON < 0s
    | GestureOwner.Mouse4 -> Win32Native.GetAsyncKeyState Win32Native.VK_XBUTTON1 < 0s
    | GestureOwner.Mouse5 -> Win32Native.GetAsyncKeyState Win32Native.VK_XBUTTON2 < 0s

let poll (state: State) =
    match state.gesture_navigation with
    | NoGestureNavigation -> ()
    | GestureNavigationActive session ->
        if ViewNavigationState.foreground_root_window () <> session.host.root_window then
            stop state
        elif session.lifetime = GestureLifetime.Hold && not (owner_button_down session.owner) then
            stop state

let press_or_log
    (state: State)
    (owner: GestureOwner)
    (action: RoutedMouseAction)
    (host: ViewportHostIdentity)
    (point: Point)
    =
    match press state owner action host point with
    | Ok() -> ()
    | Error error -> Debug.WriteLine $"RhinosCanFly mouse action: {error}"
