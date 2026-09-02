module RhinosCanFly.Platform.Win.RawNavigationCoordinator

open System
open System.Diagnostics
open Rhino
open Rhino.Geometry
open RhinosCanFly
open RhinosCanFly.Platform.Win.MouseOverrideTypes

[<Struct; RequireQualifiedAccess>]
type PointerInputDisposition =
    | Continue
    | Rebase
    | Invalidate

[<Struct>]
type DesiredNavigation =
    { host: ViewportHostIdentity
      mode: ViewportNavigation.Operation
      pivot_center: Point3d voption }

type ActiveNavigation =
    { transport: RawViewNavigationSession.Session
      requested: DesiredNavigation
      mouse_config: ViewportNavigation.MouseConfig
      timeline: InputAccumulator.TimelineEvent array
      mutable pointer_input_valid: bool
      pivot_drag: PivotDragState voption
      mutable parallel_zoom_exponent_remainder: float
      mutable next_host_validation_at: int64 }

type NavigationSession =
    | StartingTransport of RawViewNavigationSession.Session
    | ActiveTransport of ActiveNavigation

type State =
    { navigation: MouseOverrideTypes.State
      right_click: RightClickTransitions.RightClickState
      request_exit: unit -> unit
      log_exception: string -> exn -> unit
      mutable session: NavigationSession option }

let hostValidationIntervalTicks = max 1L (Stopwatch.Frequency / 10L)

let create
    (navigation: MouseOverrideTypes.State)
    (rightClick: RightClickTransitions.RightClickState)
    (requestExit: unit -> unit)
    (logException: string -> exn -> unit)
    =
    { navigation = navigation
      right_click = rightClick
      request_exit = requestExit
      log_exception = logException
      session = None }

let active_host (state: State) =
    match state.session with
    | Some(ActiveTransport active) when active.transport.IsActive -> ValueSome active.requested.host
    | Some(StartingTransport transport) when transport.IsActive -> ValueSome transport.Host
    | Some _
    | None ->
        match RightClickTransitions.direct_navigation_host state.right_click with
        | ValueSome host -> ValueSome host
        | ValueNone -> MouseOverrideState.navigation_host state.navigation

let desired (state: State) =
    let navigation = state.navigation
    let rightClick = state.right_click

    if navigation.lifecycle <> Available || navigation.navigation_exit_requested then
        ValueNone
    else
        match RightClickTransitions.parallel_zoom_host rightClick with
        | ValueSome host ->
            ValueSome
                { host = host
                  mode = ViewportNavigation.Operation.ParallelZoom
                  pivot_center = ValueNone }
        | ValueNone ->
            match RightClickTransitions.parallel_pan_host rightClick with
            | ValueSome host ->
                ValueSome
                    { host = host
                      mode = ViewportNavigation.Operation.ParallelPan
                      pivot_center = ValueNone }
            | ValueNone ->
                match navigation.gesture_navigation with
                | GestureNavigationActive session ->
                    match session.mode with
                    | ViewNavigationMode.Pivot ->
                        ValueSome
                            { host = session.host
                              mode = ViewportNavigation.Operation.Pivot
                              pivot_center = ValueSome session.pivot_center }
                    | ViewNavigationMode.Pan ->
                        ValueSome
                            { host = session.host
                              mode = ViewportNavigation.Operation.Pan
                              pivot_center = ValueNone }
                | NoGestureNavigation ->
                    match navigation.view_latch with
                    | ViewLatchActive session ->
                        match session.mode with
                        | ViewNavigationMode.Pivot ->
                            ValueSome
                                { host = session.host
                                  mode = ViewportNavigation.Operation.Pivot
                                  pivot_center = ValueSome session.pivot_center }
                        | ViewNavigationMode.Pan ->
                            ValueSome
                                { host = session.host
                                  mode = ViewportNavigation.Operation.Pan
                                  pivot_center = ValueNone }
                    | NoViewLatch
                    | WaitingForRelease _ -> ValueNone

let same_navigation (left: DesiredNavigation) (right: DesiredNavigation) =
    left.host = right.host && left.mode = right.mode

let stop (state: State) =
    match state.session with
    | None -> Ok()
    | Some session ->
        let transport =
            match session with
            | StartingTransport transport -> transport
            | ActiveTransport active ->
                active.pointer_input_valid <- false
                active.transport

        let result = transport.Stop()

        if transport.CleanupComplete then
            state.session <- None

        result

let press_requires_pointer_rebase
    (state: State)
    (host: ViewportHostIdentity)
    (action: RoutedMouseAction)
    (result: GestureNavigationTransitions.PressResult)
    =
    match result with
    | GestureNavigationTransitions.Applied pointerRebaseRequired ->
        match action with
        | RoutedMouseAction.Retarget _ ->
            match state.session with
            | Some(ActiveTransport active) when active.requested.host = host ->
                GestureNavigationTransitions.update_active_pivot_center
                    state.navigation
                    host
                    active.transport.Viewport.CameraTarget
            | Some _
            | None -> ()
        | RoutedMouseAction.Off
        | RoutedMouseAction.TogglePivot
        | RoutedMouseAction.HoldPivot
        | RoutedMouseAction.TogglePan
        | RoutedMouseAction.HoldPan -> ()

        pointerRebaseRequired
    | GestureNavigationTransitions.Deferred -> true
    | GestureNavigationTransitions.Failed error ->
        Debug.WriteLine $"RhinosCanFly mouse action: {error}"
        true

let handle_right_down
    (state: State)
    (host: ViewportHostIdentity)
    (screenPoint: System.Drawing.Point)
    (modifiers: MouseModifiers)
    =
    let navigation = state.navigation
    let rightClick = state.right_click
    rightClick.button_ownership <- Owned

    match RightClickTransitions.requested_gesture_action navigation modifiers with
    | ValueSome action ->
        let result =
            GestureNavigationTransitions.press navigation GestureOwner.ModifiedRightClick action host screenPoint

        press_requires_pointer_rebase state host action result
    | ValueNone when navigation.routing.actions.exit_on_mouse_right ->
        state.request_exit ()
        true
    | ValueNone -> false

let handle_right_up (state: State) =
    state.right_click.button_ownership <- NotOwned
    RightClickTransitions.clear_action state.right_click
    GestureNavigationTransitions.release state.navigation GestureOwner.ModifiedRightClick

let handle_side_down
    (state: State)
    (button: SideButton)
    (host: ViewportHostIdentity)
    (screenPoint: System.Drawing.Point)
    =
    let navigation = state.navigation
    let action = MouseOverrideState.action_for navigation button
    MouseOverrideState.set_hook_button_ownership navigation button Owned

    let result =
        GestureNavigationTransitions.press navigation (SideButtonTransitions.owner button) action host screenPoint

    press_requires_pointer_rebase state host action result

let handle_side_up (state: State) (button: SideButton) =
    MouseOverrideState.set_hook_button_ownership state.navigation button NotOwned
    GestureNavigationTransitions.release state.navigation (SideButtonTransitions.owner button)

let reset_active_pivot (active: ActiveNavigation) (center: Point3d) =
    match active.pivot_drag with
    | ValueSome drag -> ViewportNavigation.reset_pivot_drag active.transport.Viewport active.mouse_config center drag
    | ValueNone -> ()

let disposition_after_button (state: State) (active: ActiveNavigation) (pointerRebaseRequired: bool) =
    match desired state with
    | ValueSome requested when same_navigation requested active.requested ->
        let centerChanged =
            match requested.pivot_center, active.pivot_drag with
            | ValueSome center, ValueSome drag when center.IsValid && center <> drag.center ->
                drag.center <- center
                true
            | ValueSome _, ValueSome _
            | ValueSome _, ValueNone
            | ValueNone, ValueSome _
            | ValueNone, ValueNone -> false

        if pointerRebaseRequired || centerChanged then
            PointerInputDisposition.Rebase
        else
            PointerInputDisposition.Continue
    | ValueSome _
    | ValueNone -> PointerInputDisposition.Invalidate

let handle_button
    (state: State)
    (active: ActiveNavigation)
    (transition: RawMouseButtonTransition)
    (screenPoint: System.Drawing.Point)
    =
    try
        let mutable pointerRebaseRequired = false

        match transition.event with
        | RawMouseButtonEvent.LeftUp when state.navigation.routing.actions.exit_on_mouse_left -> state.request_exit ()
        | RawMouseButtonEvent.RightDown ->
            pointerRebaseRequired <- handle_right_down state active.requested.host screenPoint transition.modifiers
        | RawMouseButtonEvent.RightUp -> handle_right_up state
        | RawMouseButtonEvent.MiddleDown ->
            pointerRebaseRequired <- handle_side_down state Middle active.requested.host screenPoint
        | RawMouseButtonEvent.MiddleUp -> handle_side_up state Middle
        | RawMouseButtonEvent.Mouse4Down ->
            pointerRebaseRequired <- handle_side_down state Mouse4 active.requested.host screenPoint
        | RawMouseButtonEvent.Mouse4Up -> handle_side_up state Mouse4
        | RawMouseButtonEvent.Mouse5Down ->
            pointerRebaseRequired <- handle_side_down state Mouse5 active.requested.host screenPoint
        | RawMouseButtonEvent.Mouse5Up -> handle_side_up state Mouse5
        | RawMouseButtonEvent.None
        | RawMouseButtonEvent.LeftDown
        | RawMouseButtonEvent.LeftUp -> ()
        | _ -> invalidOp "Raw mouse button events must be delivered one at a time."

        MouseOverrideState.keep_timer_running state.navigation
        disposition_after_button state active pointerRebaseRequired
    with error ->
        state.log_exception "raw navigation buttons" error
        state.request_exit ()
        PointerInputDisposition.Invalidate

let validate_host (state: State) (active: ActiveNavigation) =
    let now = Stopwatch.GetTimestamp()

    if now < active.next_host_validation_at then
        true
    else
        active.next_host_validation_at <- now + hostValidationIntervalTicks

        if RawViewNavigationSession.view_matches_host active.requested.host active.transport.View then
            true
        else
            active.pointer_input_valid <- false
            state.request_exit ()
            false

let apply_motion (state: State) (active: ActiveNavigation) (dx: int64) (dy: int64) =
    let parallelZoomPending =
        active.requested.mode = ViewportNavigation.Operation.ParallelZoom
        && active.parallel_zoom_exponent_remainder <> 0.

    if
        (dx = 0L && dy = 0L && not parallelZoomPending)
        || not (validate_host state active)
    then
        false
    else
        match active.requested.mode with
        | ViewportNavigation.Operation.Pivot ->
            match active.pivot_drag with
            | ValueSome drag -> ViewportNavigation.apply_pivot active.transport.Viewport drag dx dy
            | ValueNone -> invalidOp "The active pivot has no drag state."
        | ViewportNavigation.Operation.Pan
        | ViewportNavigation.Operation.ParallelPan ->
            ViewportNavigation.apply_pan active.transport.Viewport active.mouse_config dx dy
        | ViewportNavigation.Operation.ParallelZoom ->
            let requestedExponent =
                active.parallel_zoom_exponent_remainder
                + ViewportNavigation.parallel_zoom_exponent dy

            if requestedExponent = 0. then
                false
            else
                let appliedExponent = max -0.25 (min 0.25 requestedExponent)

                if active.transport.Viewport.Magnify(Math.Exp appliedExponent, true) then
                    let remaining = requestedExponent - appliedExponent

                    active.parallel_zoom_exponent_remainder <- if abs remaining < 0.000000000001 then 0. else remaining

                    true
                else
                    active.parallel_zoom_exponent_remainder <- 0.
                    active.pointer_input_valid <- false
                    Debug.WriteLine "RhinosCanFly parallel zoom was rejected by Rhino."
                    state.request_exit ()
                    false

let apply_wheel (state: State) (active: ActiveNavigation) (delta: int64) =
    let wheelSteps = PlatformInput.wheel_zoom_steps delta

    if wheelSteps = 0. || not (validate_host state active) then
        false
    else
        let magnification = ViewportNavigation.wheel_magnification wheelSteps

        let changed =
            magnification <> 1.
            && active.transport.Viewport.Magnify(magnification, active.transport.Viewport.IsParallelProjection)

        if changed then
            match active.pivot_drag with
            | ValueSome drag -> reset_active_pivot active drag.center
            | ValueNone -> ()

        changed

let observe_release (state: State) (event: RawMouseButtonEvent) =
    match event with
    | RawMouseButtonEvent.RightUp -> handle_right_up state
    | RawMouseButtonEvent.MiddleUp -> handle_side_up state Middle
    | RawMouseButtonEvent.Mouse4Up -> handle_side_up state Mouse4
    | RawMouseButtonEvent.Mouse5Up -> handle_side_up state Mouse5
    | RawMouseButtonEvent.None
    | RawMouseButtonEvent.LeftDown
    | RawMouseButtonEvent.LeftUp
    | RawMouseButtonEvent.RightDown
    | RawMouseButtonEvent.MiddleDown
    | RawMouseButtonEvent.Mouse4Down
    | RawMouseButtonEvent.Mouse5Down -> ()
    | _ -> ()

let discard_pointer_input (active: ActiveNavigation) =
    match active.pivot_drag with
    | ValueSome drag -> reset_active_pivot active drag.center
    | ValueNone -> ()

    active.parallel_zoom_exponent_remainder <- 0.
    active.transport.DiscardPointerInput()

let drain (state: State) =
    match state.session with
    | Some(ActiveTransport active) ->
        match active.transport.Drain active.timeline with
        | ValueNone -> ()
        | ValueSome result ->
            if result.overflowed then
                active.pointer_input_valid <- false
                Debug.WriteLine "RhinosCanFly raw view navigation timeline overflowed."
                state.request_exit ()
            else
                let mutable acceptPointerInput = true
                let mutable viewChanged = false
                let mutable index = 0

                while index < result.count do
                    let event = active.timeline[index]

                    match event.kind with
                    | InputAccumulator.TimelineEventKind.Movement when active.pointer_input_valid && acceptPointerInput ->
                        viewChanged <- apply_motion state active event.dx event.dy || viewChanged
                    | InputAccumulator.TimelineEventKind.Wheel when active.pointer_input_valid && acceptPointerInput ->
                        viewChanged <- apply_wheel state active event.wheel || viewChanged
                    | InputAccumulator.TimelineEventKind.RawMouseButton when active.pointer_input_valid ->
                        match handle_button state active event.button active.transport.OriginalCursor with
                        | PointerInputDisposition.Continue -> ()
                        | PointerInputDisposition.Rebase -> discard_pointer_input active
                        | PointerInputDisposition.Invalidate ->
                            acceptPointerInput <- false
                            active.pointer_input_valid <- false
                            discard_pointer_input active
                    | InputAccumulator.TimelineEventKind.RawMouseButton -> observe_release state event.button.event
                    | InputAccumulator.TimelineEventKind.Movement
                    | InputAccumulator.TimelineEventKind.Wheel
                    | InputAccumulator.TimelineEventKind.KeyboardActions -> ()
                    | _ -> invalidOp "The raw view navigation timeline contains an unknown event."

                    index <- index + 1

                if
                    active.pointer_input_valid
                    && acceptPointerInput
                    && active.parallel_zoom_exponent_remainder <> 0.
                then
                    viewChanged <- apply_motion state active 0L 0L || viewChanged

                if viewChanged then
                    active.transport.View.Redraw()

                if active.pointer_input_valid && active.parallel_zoom_exponent_remainder <> 0. then
                    active.transport.RequestDrain()
    | Some(StartingTransport _)
    | None -> ()

let start (state: State) (requested: DesiredNavigation) =
    let failed = Action state.request_exit

    match RawViewNavigationSession.start requested.host requested.mode failed with
    | Error error ->
        match GestureNavigationTransitions.rollback_start state.navigation with
        | Ok() -> Error error
        | Error rollbackError -> Error $"{error}; {rollbackError}"
    | Ok transport ->
        state.session <- Some(StartingTransport transport)

        try
            let pivotCenter =
                match requested.pivot_center with
                | ValueSome center when center.IsValid -> center
                | ValueSome _
                | ValueNone -> transport.Viewport.CameraTarget

            let mouseConfig =
                ViewportNavigation.mouse_config
                    state.navigation.routing.actions.view_navigation_mouse
                    transport.Viewport.IsParallelProjection

            let pivotDrag =
                if requested.mode = ViewportNavigation.Operation.Pivot then
                    ValueSome(ViewportNavigation.create_pivot_drag transport.Viewport mouseConfig pivotCenter)
                else
                    ValueNone

            let active =
                { transport = transport
                  requested = requested
                  mouse_config = mouseConfig
                  timeline = InputAccumulator.timeline_buffer ()
                  pointer_input_valid = true
                  pivot_drag = pivotDrag
                  parallel_zoom_exponent_remainder = 0.
                  next_host_validation_at = Stopwatch.GetTimestamp() + hostValidationIntervalTicks }

            state.session <- Some(ActiveTransport active)
            transport.Attach(Action(fun () -> drain state))
            transport.RequestDrain()
            Ok()
        with error ->
            match state.session with
            | Some(ActiveTransport active) -> active.pointer_input_valid <- false
            | Some(StartingTransport _)
            | None -> ()

            let cleanup = transport.Stop()

            if transport.CleanupComplete then
                state.session <- None

            let rollback = GestureNavigationTransitions.rollback_start state.navigation
            let errors = ResizeArray<string>()
            errors.Add $"Could not start raw view navigation: {error.Message}"

            match cleanup with
            | Ok() -> ()
            | Error cleanupError -> errors.Add $"cleanup failed: {cleanupError}"

            match rollback with
            | Ok() -> ()
            | Error rollbackError -> errors.Add $"rollback failed: {rollbackError}"

            Error(String.concat "; " errors)

let reconcile (state: State) =
    match desired state with
    | ValueNone -> stop state
    | ValueSome requested ->
        match state.session with
        | Some(ActiveTransport current) when
            current.pointer_input_valid
            && current.transport.Matches(requested.host, requested.mode)
            ->
            match requested.pivot_center, current.pivot_drag with
            | ValueSome center, ValueSome drag when center.IsValid && center <> drag.center ->
                drag.center <- center
                discard_pointer_input current
            | ValueSome _, ValueSome _
            | ValueSome _, ValueNone
            | ValueNone, ValueSome _
            | ValueNone, ValueNone -> ()

            Ok()
        | Some _ ->
            match stop state with
            | Error error ->
                match GestureNavigationTransitions.rollback_start state.navigation with
                | Ok() -> Error error
                | Error rollbackError -> Error $"{error}; {rollbackError}"
            | Ok() -> start state requested
        | None -> start state requested

let release (state: State) =
    RightClickTransitions.clear_direct_navigation state.right_click
    let rawResult = stop state
    let viewResult = MouseOverrideState.release_all state.navigation

    match viewResult with
    | Error error ->
        match rawResult with
        | Error rawError -> Error $"{error}; raw navigation: {rawError}"
        | Ok() -> Error error
    | Ok() -> rawResult

let is_present (state: State) = Option.isSome state.session

let captures_button_messages (state: State) =
    let transport =
        match state.session with
        | Some(StartingTransport transport) -> Some transport
        | Some(ActiveTransport active) -> Some active.transport
        | None -> None

    match transport with
    | Some current when current.IsActive ->
        match current.RawInputRegistrationIsCurrent() with
        | Ok true -> true
        | Ok false ->
            match state.session with
            | Some(ActiveTransport active) -> active.pointer_input_valid <- false
            | Some(StartingTransport _)
            | None -> ()

            state.request_exit ()
            true
        | Error error ->
            match state.session with
            | Some(ActiveTransport active) -> active.pointer_input_valid <- false
            | Some(StartingTransport _)
            | None -> ()

            Debug.WriteLine $"RhinosCanFly could not verify raw-input ownership: {error}"
            state.request_exit ()
            true
    | Some _
    | None -> false
