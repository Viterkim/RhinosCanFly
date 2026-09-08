module RhinosCanFly.Platform.Win.ViewLatchTransitions

open System
open System.Diagnostics
open RhinosCanFly
open RhinosCanFly.Platform.Win.MouseOverrideTypes

let release (state: State) =
    match state.view_latch with
    | NoViewLatch -> Ok()
    | (WaitingForRelease _ | ViewLatchActive _) as active ->
        state.view_latch <- NoViewLatch
        MouseOverrideState.stop_timer_if_idle state
        MouseOverrideState.complete_view_latch active

let complete_or_log (latch: ViewLatch) =
    match MouseOverrideState.complete_view_latch latch with
    | Ok() -> ()
    | Error error -> Debug.WriteLine $"RhinosCanFly latched view manipulation: {error}"

let input_released () =
    not (Win32.key_down Win32Native.VK_RBUTTON)
    && not (MouseOverrideState.shift_down ())
    && not (MouseOverrideState.alt_down ())
    && not (MouseOverrideState.control_down ())

let activate (state: State) (session: ViewLatchSession) =
    if MouseOverrideState.gesture_navigation_engaged state then
        Error "Another view navigation mode is already active."
    else
        state.view_latch <- ViewLatchActive session
        MouseOverrideState.keep_timer_running state
        Ok()

let start
    (state: State)
    (host: ViewportHostIdentity)
    (mode: ViewNavigationMode)
    (target: Rhino.Geometry.Point3d)
    (rollback: unit -> Result<unit, string>)
    (completion: Action option)
    =
    let view = Rhino.Display.RhinoView.FromRuntimeSerialNumber host.view_serial_number

    if isNull view || isNull view.Document then
        Error "The navigation viewport is unavailable."
    else
        let session =
            { host = host
              mode = mode
              pivot_center = target
              startup_rollback = Some rollback
              completion = completion }

        if input_released () then
            activate state session
        else
            state.view_latch <- WaitingForRelease session
            MouseOverrideState.keep_timer_running state
            Ok()

let update_with (foreground: RootWindow) (released: bool) (state: State) =
    match state.view_latch with
    | NoViewLatch -> ()
    | WaitingForRelease pending ->
        if foreground <> pending.host.root_window then
            state.view_latch <- NoViewLatch
            MouseOverrideState.stop_timer_if_idle state
            complete_or_log (WaitingForRelease pending)
        elif released then
            match activate state pending with
            | Ok() -> ()
            | Error error ->
                state.view_latch <- NoViewLatch
                MouseOverrideState.stop_timer_if_idle state
                complete_or_log (WaitingForRelease pending)
                Debug.WriteLine $"RhinosCanFly latched view manipulation: {error}"
    | ViewLatchActive session ->
        if foreground <> session.host.root_window then
            match release state with
            | Ok() -> ()
            | Error error -> Debug.WriteLine $"RhinosCanFly latched view manipulation: {error}"

let update (state: State) =
    match state.view_latch with
    | NoViewLatch -> ()
    | _ -> update_with (MouseOverrideState.foreground_root_window ()) (input_released ()) state

let current_mode (state: State) =
    match state.view_latch with
    | NoViewLatch -> None
    | WaitingForRelease pending -> Some pending.mode
    | ViewLatchActive session -> Some session.mode

let is_mode (state: State) (mode: ViewNavigationMode) = current_mode state = Some mode

let start_or_switch
    (state: State)
    (host: ViewportHostIdentity)
    (mode: ViewNavigationMode)
    (rollback: unit -> Result<unit, string>)
    (completion: Action option)
    =
    if state.lifecycle <> Available then
        Error "Mouse button overrides are unavailable."
    else
        match current_mode state with
        | Some current when current = mode -> Ok()
        | None when not (MouseOverrideState.gesture_navigation_engaged state) ->
            let can_apply = MouseOverrideState.begin_action state

            match state.routing.prepare_navigation host NavigationTargetPoint.ViewCenter mode can_apply with
            | _ when not (can_apply ()) -> Error "Navigation was cancelled during preparation."
            | Error error -> Error error
            | Ok(struct (prepared, target)) -> start state prepared mode target rollback completion
        | Some _
        | None ->
            match MouseOverrideState.release_all state with
            | Error error -> Error error
            | Ok() ->
                let can_apply = MouseOverrideState.begin_action state

                match state.routing.prepare_navigation host NavigationTargetPoint.ViewCenter mode can_apply with
                | _ when not (can_apply ()) -> Error "Navigation was cancelled during preparation."
                | Error error -> Error error
                | Ok(struct (prepared, target)) -> start state prepared mode target rollback completion

let stop (state: State) (mode: ViewNavigationMode) =
    if state.lifecycle <> Available then
        Error "Mouse button overrides are unavailable."
    else
        match current_mode state with
        | Some current when current = mode -> MouseOverrideState.release_all state
        | Some _
        | None -> Ok()
