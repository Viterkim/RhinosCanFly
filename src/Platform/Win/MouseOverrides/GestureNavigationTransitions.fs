module RhinosCanFly.Platform.Win.GestureNavigationTransitions

open System.Drawing
open Rhino
open Rhino.Display
open RhinosCanFly
open RhinosCanFly.Platform.Win.MouseOverrideTypes

[<Struct>]
type PressResult =
    | Applied of pointer_rebase_required: bool
    | Deferred
    | Failed of error: string

[<Struct>]
type ActionViewPreparation =
    | ActionViewReady of view: RhinoView * host: ViewportHostIdentity
    | ActionViewDeferred
    | ActionViewUnavailable of error: string

let prepare_action_view (host: ViewportHostIdentity) =
    if MouseOverrideState.foreground_root_window () <> host.root_window then
        if MouseOverrideState.try_bring_root_window_to_foreground host.root_window then
            ActionViewDeferred
        else
            ActionViewUnavailable "The navigation window could not be activated."
    else
        let view = RhinoView.FromRuntimeSerialNumber host.view_serial_number
        let document = if isNull view then null else view.Document
        let activeDocument = RhinoDoc.ActiveDoc
        let (ViewWindowHandle expectedWindow) = host.view_window

        if
            isNull view
            || isNull document
            || isNull activeDocument
            || document.RuntimeSerialNumber <> host.document_serial_number
            || activeDocument.RuntimeSerialNumber <> host.document_serial_number
            || view.Handle <> expectedWindow
            || MouseOverrideState.root_window view.Handle <> host.root_window
        then
            ActionViewUnavailable "The navigation viewport is unavailable."
        else
            let activeView = document.Views.ActiveView

            if isNull activeView || activeView.RuntimeSerialNumber <> view.RuntimeSerialNumber then
                document.Views.ActiveView <- view
                ActionViewDeferred
            else
                ActionViewReady(
                    view,
                    { document_serial_number = document.RuntimeSerialNumber
                      view_serial_number = view.RuntimeSerialNumber
                      viewport_id = view.ActiveViewportID
                      view_window = ViewWindowHandle view.Handle
                      root_window = MouseOverrideState.root_window view.Handle }
                )

let complete_view_latch (state: State) =
    let previous = state.view_latch
    state.view_latch <- NoViewLatch
    MouseOverrideState.complete_view_latch previous

let uses_cursor_outside_flight (state: State) (owner: GestureOwner) =
    match owner with
    | GestureOwner.ModifiedRightClick -> true
    | GestureOwner.Middle -> state.routing.actions.outside_flight_cursor.middle
    | GestureOwner.Mouse4 -> state.routing.actions.outside_flight_cursor.mouse4
    | GestureOwner.Mouse5 -> state.routing.actions.outside_flight_cursor.mouse5

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
    MouseOverrideState.stop_timer_if_idle state

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

                    MouseOverrideState.keep_timer_running state
                    Ok()

let press
    (state: State)
    (owner: GestureOwner)
    (action: RoutedMouseAction)
    (host: ViewportHostIdentity)
    (screenPoint: Point)
    =
    match action with
    | RoutedMouseAction.Off -> Applied false
    | RoutedMouseAction.Retarget _
    | RoutedMouseAction.TogglePivot
    | RoutedMouseAction.HoldPivot
    | RoutedMouseAction.TogglePan
    | RoutedMouseAction.HoldPan ->
        match prepare_action_view host with
        | ActionViewDeferred -> Deferred
        | ActionViewUnavailable error -> Failed error
        | ActionViewReady(view, activeHost) ->
            let result =
                match action with
                | RoutedMouseAction.Retarget mode ->
                    state.routing.retarget activeHost (client_target_point state owner view screenPoint) mode
                | RoutedMouseAction.TogglePivot ->
                    begin_navigation state owner activeHost screenPoint ViewNavigationMode.Pivot GestureLifetime.Toggle
                | RoutedMouseAction.HoldPivot ->
                    begin_navigation state owner activeHost screenPoint ViewNavigationMode.Pivot GestureLifetime.Hold
                | RoutedMouseAction.TogglePan ->
                    begin_navigation state owner activeHost screenPoint ViewNavigationMode.Pan GestureLifetime.Toggle
                | RoutedMouseAction.HoldPan ->
                    begin_navigation state owner activeHost screenPoint ViewNavigationMode.Pan GestureLifetime.Hold
                | RoutedMouseAction.Off -> Ok()

            match result with
            | Ok() -> Applied true
            | Error error -> Failed error

let release (state: State) (owner: GestureOwner) =
    match state.gesture_navigation with
    | GestureNavigationActive current when current.owner = owner && current.lifetime = GestureLifetime.Hold ->
        stop state
    | GestureNavigationActive _
    | NoGestureNavigation -> ()

let update_active_pivot_center (state: State) (host: ViewportHostIdentity) (target: Rhino.Geometry.Point3d) =
    match state.gesture_navigation with
    | GestureNavigationActive session when session.host = host && session.mode = ViewNavigationMode.Pivot ->
        state.gesture_navigation <- GestureNavigationActive { session with pivot_center = target }
    | GestureNavigationActive _
    | NoGestureNavigation -> ()

    match state.view_latch with
    | ViewLatchActive session when session.host = host && session.mode = ViewNavigationMode.Pivot ->
        state.view_latch <- ViewLatchActive { session with pivot_center = target }
    | WaitingForRelease session when session.host = host && session.mode = ViewNavigationMode.Pivot ->
        state.view_latch <- WaitingForRelease { session with pivot_center = target }
    | NoViewLatch
    | WaitingForRelease _
    | ViewLatchActive _ -> ()

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
        if MouseOverrideState.foreground_root_window () <> session.host.root_window then
            stop state
        elif session.lifetime = GestureLifetime.Hold && not (owner_button_down session.owner) then
            stop state
