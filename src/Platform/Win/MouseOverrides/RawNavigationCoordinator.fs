module RhinosCanFly.Platform.Win.RawNavigationCoordinator

open System
open System.Diagnostics
open System.Drawing
open RhinosCanFly
open RhinosCanFly.Platform.Win.ViewNavigationTypes

type State =
    { navigation: ViewNavigationTypes.State
      right_click: RightClickTransitions.RightClickState
      request_exit: unit -> unit
      log_exception: string -> exn -> unit
      mutable session: RawViewNavigation.Session option }

[<Struct>]
type DesiredNavigation =
    { host: ViewportHostIdentity
      mode: RawViewNavigation.Mode
      pivot_center: Rhino.Geometry.Point3d voption }

let create
    (navigation: ViewNavigationTypes.State)
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
    match RightClickTransitions.direct_navigation_host state.right_click with
    | ValueSome host -> ValueSome host
    | ValueNone -> ViewNavigationState.navigation_host state.navigation

let desired (state: State) =
    let navigation = state.navigation
    let rightClick = state.right_click

    if navigation.lifecycle <> Available then
        ValueNone
    else
        match RightClickTransitions.parallel_zoom_host rightClick with
        | ValueSome host ->
            ValueSome
                { host = host
                  mode = RawViewNavigation.Mode.ParallelZoom
                  pivot_center = ValueNone }
        | ValueNone ->
            match RightClickTransitions.parallel_pan_host rightClick with
            | ValueSome host ->
                ValueSome
                    { host = host
                      mode = RawViewNavigation.Mode.ParallelPan
                      pivot_center = ValueNone }
            | ValueNone ->
                match navigation.gesture_navigation with
                | GestureNavigationActive session ->
                    match session.mode with
                    | ViewNavigationMode.Pivot ->
                        ValueSome
                            { host = session.host
                              mode = RawViewNavigation.Mode.Pivot
                              pivot_center = ValueSome session.pivot_center }
                    | ViewNavigationMode.Pan ->
                        ValueSome
                            { host = session.host
                              mode = RawViewNavigation.Mode.Pan
                              pivot_center = ValueNone }
                | NoGestureNavigation ->
                    match navigation.view_latch with
                    | PivotActive session ->
                        ValueSome
                            { host = session.host
                              mode = RawViewNavigation.Mode.Pivot
                              pivot_center = ValueSome session.pivot_center }
                    | PanActive session ->
                        ValueSome
                            { host = session.host
                              mode = RawViewNavigation.Mode.Pan
                              pivot_center = ValueNone }
                    | NoViewLatch
                    | WaitingForRelease _ -> ValueNone

let stop (state: State) =
    match state.session with
    | None -> Ok()
    | Some session ->
        let result = session.Stop()

        if session.CleanupComplete then
            state.session <- None

        result

let handle_right_down (state: State) (host: ViewportHostIdentity) (screenPoint: Point) =
    let navigation = state.navigation
    let rightClick = state.right_click
    rightClick.button_ownership <- Owned

    match RightClickTransitions.requested_gesture_action navigation (RightClickTransitions.modifiers ()) with
    | ValueSome action ->
        GestureNavigationTransitions.press_or_log
            navigation
            GestureOwner.ModifiedRightClick
            action
            host
            screenPoint
    | ValueNone when navigation.routing.exit_on_mouse_right -> state.request_exit ()
    | ValueNone -> ()

let handle_right_up (state: State) =
    state.right_click.button_ownership <- NotOwned
    RightClickTransitions.clear_action state.right_click
    GestureNavigationTransitions.release state.navigation GestureOwner.ModifiedRightClick

let handle_side_down
    (state: State)
    (button: SideButton)
    (host: ViewportHostIdentity)
    (screenPoint: Point)
    =
    let navigation = state.navigation
    ViewNavigationState.set_hook_button_ownership navigation button Owned

    GestureNavigationTransitions.press_or_log
        navigation
        (SideButtonTransitions.owner button)
        (ViewNavigationState.action_for navigation button)
        host
        screenPoint

let handle_side_up (state: State) (button: SideButton) =
    ViewNavigationState.set_hook_button_ownership state.navigation button NotOwned
    GestureNavigationTransitions.release state.navigation (SideButtonTransitions.owner button)

let handle_button
    (state: State)
    (host: ViewportHostIdentity)
    (event: RawMouseButtonEvent)
    (screenPoint: Point)
    =
    try
        match event with
        | RawMouseButtonEvent.LeftUp when state.navigation.routing.exit_on_mouse_left -> state.request_exit ()
        | RawMouseButtonEvent.RightDown -> handle_right_down state host screenPoint
        | RawMouseButtonEvent.RightUp -> handle_right_up state
        | RawMouseButtonEvent.MiddleDown -> handle_side_down state Middle host screenPoint
        | RawMouseButtonEvent.MiddleUp -> handle_side_up state Middle
        | RawMouseButtonEvent.Mouse4Down -> handle_side_down state Mouse4 host screenPoint
        | RawMouseButtonEvent.Mouse4Up -> handle_side_up state Mouse4
        | RawMouseButtonEvent.Mouse5Down -> handle_side_down state Mouse5 host screenPoint
        | RawMouseButtonEvent.Mouse5Up -> handle_side_up state Mouse5
        | RawMouseButtonEvent.None
        | RawMouseButtonEvent.LeftDown
        | RawMouseButtonEvent.LeftUp -> ()
        | _ -> invalidOp "Raw mouse button events must be delivered one at a time."

        ViewNavigationState.keep_timer_running state.navigation
    with error ->
        state.log_exception "raw navigation buttons" error
        state.request_exit ()

let start (state: State) (requested: DesiredNavigation) =
    let failed = Action state.request_exit

    let buttonEvents =
        Action<RawMouseButtonEvent, Point>(fun (event: RawMouseButtonEvent) (point: Point) ->
            handle_button state requested.host event point)

    match
        RawViewNavigation.start
            requested.host
            requested.mode
            requested.pivot_center
            state.navigation.routing.view_navigation_mouse
            buttonEvents
            failed
    with
    | Error error ->
        match GestureNavigationTransitions.rollback_start state.navigation with
        | Ok() -> Error error
        | Error rollbackError -> Error $"{error}; {rollbackError}"
    | Ok session ->
        state.session <- Some session
        Ok()

let reconcile (state: State) =
    match desired state with
    | ValueNone -> stop state
    | ValueSome requested ->
        match state.session with
        | Some current when current.Matches(requested.host, requested.mode) ->
            current.UpdatePivotCenter requested.pivot_center
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
    let viewResult = ViewNavigationState.release_all state.navigation

    match viewResult with
    | Error error ->
        match rawResult with
        | Error rawError -> Error $"{error}; raw navigation: {rawError}"
        | Ok() -> Error error
    | Ok() -> rawResult

let is_present (state: State) = Option.isSome state.session

let captures_button_messages (state: State) =
    match state.session with
    | Some session when session.IsActive ->
        match session.RawInputRegistrationIsCurrent() with
        | Ok true -> true
        | Ok false ->
            state.request_exit ()
            true
        | Error error ->
            Debug.WriteLine $"RhinosCanFly could not verify raw-input ownership: {error}"
            state.request_exit ()
            true
    | Some _
    | None -> false
