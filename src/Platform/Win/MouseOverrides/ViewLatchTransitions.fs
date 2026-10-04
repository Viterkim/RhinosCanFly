module RhinosCanFly.Platform.Win.ViewLatchTransitions

open System
open System.Diagnostics
open RhinosCanFly
open RhinosCanFly.Platform.Win.MouseOverrideTypes

let release (state: State) =
    match state.view_latch with
    | NoViewLatch -> Ok()
    | (WaitingForRelease _ | ViewLatchActive _) as active ->
        let revision = state.navigation_revision
        state.view_latch <- NoViewLatch
        MouseOverrideState.stop_timer_if_idle state
        MouseOverrideState.complete_view_latch state revision active

let complete_or_log (state: State) (revision: int64) (latch: ViewLatch) =
    match MouseOverrideState.complete_view_latch state revision latch with
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
        MouseOverrideState.keep_timer_running state
        state.view_latch <- ViewLatchActive session
        Ok()

let start
    (state: State)
    (host: ViewportHostIdentity)
    (mode: ViewNavigationMode)
    (target: Rhino.Geometry.Point3d)
    (can_apply: unit -> bool)
    (rollback: int64 -> Result<unit, string>)
    (completion: Action<unit -> bool> option)
    =
    let view = Rhino.Display.RhinoView.FromRuntimeSerialNumber host.view_serial_number

    if not (PlatformInput.viewport_host_is_active host view) then
        Error "The navigation viewport is unavailable."
    elif not (can_apply ()) then
        Error "Navigation was cancelled during viewport validation."
    else
        let session =
            { host = host
              mode = mode
              pivot_center = target
              startup_rollback = Some rollback
              completion = completion }

        if input_released () then
            activate state session |> Result.map (fun (_: unit) -> session)
        else
            MouseOverrideState.keep_timer_running state
            state.view_latch <- WaitingForRelease session
            Ok session

let update_with (foreground: RootWindow) (released: bool) (state: State) =
    let revision = state.navigation_revision

    match state.view_latch with
    | NoViewLatch -> ()
    | WaitingForRelease pending ->
        if foreground <> pending.host.root_window then
            state.view_latch <- NoViewLatch
            MouseOverrideState.stop_timer_if_idle state
            complete_or_log state revision (WaitingForRelease pending)
        elif released then
            match activate state pending with
            | Ok() -> ()
            | Error error ->
                state.view_latch <- NoViewLatch
                MouseOverrideState.stop_timer_if_idle state
                complete_or_log state revision (WaitingForRelease pending)
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

let prepare_start
    (state: State)
    (host: ViewportHostIdentity)
    (mode: ViewNavigationMode)
    (completion: Action<unit -> bool> option)
    =
    let can_apply = MouseOverrideState.begin_action state
    let revision = state.navigation_revision

    let struct (record_change, rollback) =
        GestureNavigationTransitions.target_rollback state host

    let result =
        try
            match
                state.routing.prepare_navigation host NavigationTargetPoint.ViewCenter mode can_apply record_change
            with
            | _ when not (can_apply ()) -> Error "Navigation was cancelled during preparation."
            | Error error -> Error error
            | Ok(struct (prepared, target)) -> start state prepared mode target can_apply rollback completion
        with error ->
            Error error.Message

    match result with
    | Ok session -> Ok session
    | Error error ->
        match rollback revision with
        | Ok() -> Error error
        | Error rollback_error -> Error $"{error}; {rollback_error}"

let start_or_switch
    (state: State)
    (host: ViewportHostIdentity)
    (mode: ViewNavigationMode)
    (completion: Action<unit -> bool> option)
    =
    if state.lifecycle <> Available then
        Error "Mouse button overrides are unavailable."
    else
        match current_mode state with
        | Some current when current = mode ->
            match state.view_latch with
            | WaitingForRelease session
            | ViewLatchActive session -> Ok(struct (session, false))
            | NoViewLatch -> Error "The navigation session disappeared."
        | _ ->
            let revision = state.navigation_revision

            let replacing =
                MouseOverrideState.view_latch_engaged state
                || MouseOverrideState.gesture_navigation_engaged state

            let released =
                if replacing then
                    MouseOverrideState.release_all state
                else
                    Ok()

            let expected_revision = if replacing then revision + 1L else revision

            match released with
            | Error error -> Error error
            | Ok() when state.navigation_revision <> expected_revision -> Error "Navigation changed during cleanup."
            | Ok() ->
                prepare_start state host mode completion
                |> Result.map (fun (session: ViewLatchSession) -> struct (session, true))

let stop (state: State) (mode: ViewNavigationMode) =
    if state.lifecycle <> Available then
        Error "Mouse button overrides are unavailable."
    else
        match current_mode state with
        | Some current when current = mode -> MouseOverrideState.release_all state
        | Some _
        | None -> Ok()
