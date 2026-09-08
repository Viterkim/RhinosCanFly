module RhinosCanFly.Platform.Win.GestureNavigationTransitions

open System.Drawing
open Rhino
open Rhino.Display
open RhinosCanFly
open RhinosCanFly.Platform.Win.MouseOverrideTypes

[<Struct>]
type PressResult =
    | Applied
    | Retargeted of outcome: ApplicationOutcome
    | Deferred
    | Failed of error: string

[<Struct>]
type ActionViewPreparation =
    | ActionViewReady of view: RhinoView * host: ViewportHostIdentity
    | ActionViewDeferred
    | ActionViewUnavailable of error: string

let prepare_action_view (host: ViewportHostIdentity) =
    let foreground_ready =
        MouseOverrideState.foreground_root_window () = host.root_window
        || MouseOverrideState.try_bring_root_window_to_foreground host.root_window

    if not foreground_ready then
        ActionViewUnavailable "The navigation window could not be activated."
    else
        let view = RhinoView.FromRuntimeSerialNumber host.view_serial_number
        let document = if isNull view then null else view.Document
        let active_document = RhinoDoc.ActiveDoc
        let (ViewWindowHandle expected_window) = host.view_window

        if
            isNull view
            || isNull document
            || isNull active_document
            || document.RuntimeSerialNumber <> host.document_serial_number
            || active_document.RuntimeSerialNumber <> host.document_serial_number
            || view.Handle <> expected_window
            || MouseOverrideState.root_window view.Handle <> host.root_window
            || view.ActiveViewportID <> host.viewport_id
        then
            ActionViewUnavailable "The navigation viewport is unavailable."
        else
            let active_view = document.Views.ActiveView

            if
                isNull active_view
                || active_view.RuntimeSerialNumber <> view.RuntimeSerialNumber
            then
                document.Views.ActiveView <- view
                ActionViewDeferred
            else
                ActionViewReady(view, host)

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

let client_target_point (state: State) (owner: GestureOwner) (view: RhinoView) (screen_point: Point) =
    if uses_cursor_outside_flight state owner then
        let point = view.ActiveViewport.ScreenToClient screen_point
        { x = point.X; y = point.Y }
    else
        let bounds = view.ActiveViewport.Bounds

        { x = bounds.Width / 2
          y = bounds.Height / 2 }

let stop (state: State) =
    state.gesture_navigation <- NoGestureNavigation
    MouseOverrideState.stop_timer_if_idle state

let restore_original_target (host: ViewportHostIdentity) (original_target: Rhino.Geometry.Point3d voption) =
    match original_target with
    | ValueNone -> Ok()
    | ValueSome target ->
        try
            match PlatformInput.try_find_host_viewport host with
            | Some viewport ->
                viewport.SetCameraTarget(target, false)
                RhinoView.FromRuntimeSerialNumber(host.view_serial_number).Redraw()
            | None -> ()

            Ok()
        with error ->
            Error $"Could not restore the navigation target: {error.Message}"

let rollback_start (state: State) =
    let session =
        match state.gesture_navigation with
        | GestureNavigationActive active -> ValueSome active
        | NoGestureNavigation -> ValueNone

    stop state

    let gesture_result =
        match session with
        | ValueSome active -> restore_original_target active.host active.original_target
        | ValueNone -> Ok()

    let latch_result = complete_view_latch state

    match gesture_result, latch_result with
    | Ok(), Ok() -> Ok()
    | Error error, Ok()
    | Ok(), Error error -> Error error
    | Error gesture_error, Error latch_error -> Error $"{gesture_error}; {latch_error}"

let begin_navigation
    (state: State)
    (can_apply: unit -> bool)
    (owner: GestureOwner)
    (host: ViewportHostIdentity)
    (screen_point: Point)
    (mode: ViewNavigationMode)
    (lifetime: GestureLifetime)
    =
    let mutable can_start = true

    match state.gesture_navigation with
    | GestureNavigationActive current when
        current.owner = owner
        && current.mode = mode
        && current.lifetime = GestureLifetime.Toggle
        ->
        stop state
        can_start <- false
    | GestureNavigationActive _ -> stop state
    | NoGestureNavigation -> ()

    if not can_start then
        Ok()
    else
        match complete_view_latch state with
        | Error error -> Error error
        | Ok() ->
            let view = RhinoView.FromRuntimeSerialNumber host.view_serial_number

            let original_target =
                if isNull view || isNull view.Document then
                    ValueNone
                else
                    ValueSome view.ActiveViewport.CameraTarget

            let target_point =
                if
                    isNull view
                    || isNull view.Document
                    || not (uses_cursor_outside_flight state owner)
                then
                    NavigationTargetPoint.ViewCenter
                else
                    NavigationTargetPoint.ClientPoint(client_target_point state owner view screen_point)

            let result =
                try
                    match state.routing.prepare_navigation host target_point mode can_apply with
                    | _ when not (can_apply ()) -> Error "Navigation was cancelled during preparation."
                    | Error error -> Error error
                    | Ok(struct (prepared, target)) ->
                        let prepared_view = RhinoView.FromRuntimeSerialNumber prepared.view_serial_number

                        if not (PlatformInput.viewport_host_is_active prepared prepared_view) then
                            Error "The navigation viewport disappeared during startup."
                        else
                            MouseOverrideState.keep_timer_running state

                            state.gesture_navigation <-
                                GestureNavigationActive
                                    { owner = owner
                                      host = prepared
                                      mode = mode
                                      lifetime = lifetime
                                      pivot_center = target
                                      original_target = original_target }

                            Ok()
                with error ->
                    Error error.Message

            match result with
            | Ok() -> Ok()
            | Error error ->
                match restore_original_target host original_target with
                | Ok() -> Error error
                | Error restore_error -> Error $"{error}; {restore_error}"

let retarget (apply: unit -> ApplicationOutcome) (can_apply: unit -> bool) =
    let outcome = apply ()

    for error in outcome.errors do
        System.Diagnostics.Debug.WriteLine $"RhinosCanFly retarget: {error}"

    Retargeted(
        if can_apply () then
            outcome
        else
            { outcome with
                source_target = ValueNone }
    )

let press
    (state: State)
    (owner: GestureOwner)
    (action: RoutedMouseAction)
    (host: ViewportHostIdentity)
    (screen_point: Point)
    =
    match action with
    | RoutedMouseAction.Off -> Applied
    | RoutedMouseAction.Retarget _
    | RoutedMouseAction.TogglePivot
    | RoutedMouseAction.HoldPivot
    | RoutedMouseAction.TogglePan
    | RoutedMouseAction.HoldPan ->
        let can_apply = MouseOverrideState.begin_action state

        match prepare_action_view host with
        | _ when not (can_apply ()) -> Failed "Navigation was cancelled during viewport activation."
        | ActionViewDeferred -> Deferred
        | ActionViewUnavailable error -> Failed error
        | ActionViewReady(view, active_host) ->
            match action with
            | RoutedMouseAction.Retarget mode ->
                retarget
                    (fun () ->
                        state.routing.retarget
                            active_host
                            (client_target_point state owner view screen_point)
                            mode
                            can_apply)
                    can_apply
            | RoutedMouseAction.Off -> Applied
            | RoutedMouseAction.TogglePivot
            | RoutedMouseAction.HoldPivot
            | RoutedMouseAction.TogglePan
            | RoutedMouseAction.HoldPan ->
                let mode =
                    match action with
                    | RoutedMouseAction.TogglePivot
                    | RoutedMouseAction.HoldPivot -> ViewNavigationMode.Pivot
                    | _ -> ViewNavigationMode.Pan

                let lifetime =
                    if RoutedMouseAction.holds_pivot action || RoutedMouseAction.holds_pan action then
                        GestureLifetime.Hold
                    else
                        GestureLifetime.Toggle

                match begin_navigation state can_apply owner active_host screen_point mode lifetime with
                | Ok() -> Applied
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
    | GestureOwner.ModifiedRightClick -> Win32.key_down Win32Native.VK_RBUTTON
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
