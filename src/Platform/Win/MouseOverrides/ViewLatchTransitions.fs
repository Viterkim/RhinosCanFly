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
    Win32Native.GetAsyncKeyState Win32Native.VK_RBUTTON >= 0s
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

let start (state: State) (host: ViewportHostIdentity) (mode: ViewNavigationMode) (completion: Action option) =
    let view = Rhino.Display.RhinoView.FromRuntimeSerialNumber host.view_serial_number

    if isNull view || isNull view.Document then
        Error "The navigation viewport is unavailable."
    else
        let session =
            { host = host
              mode = mode
              pivot_center = view.ActiveViewport.CameraTarget
              started_at = Stopwatch.GetTimestamp()
              completion = completion }

        if input_released () then
            activate state session
        else
            state.view_latch <- WaitingForRelease session
            MouseOverrideState.keep_timer_running state
            Ok()

let update (state: State) =
    match state.view_latch with
    | NoViewLatch -> ()
    | WaitingForRelease pending ->
        let timedOut = MouseOverrideState.transition_timed_out pending

        if
            MouseOverrideState.foreground_root_window () <> pending.host.root_window
            || timedOut
        then
            state.view_latch <- NoViewLatch
            MouseOverrideState.stop_timer_if_idle state
            complete_or_log (WaitingForRelease pending)
        elif input_released () then
            match activate state pending with
            | Ok() -> ()
            | Error error ->
                complete_or_log (WaitingForRelease pending)
                Debug.WriteLine $"RhinosCanFly latched view manipulation: {error}"
    | ViewLatchActive session ->
        if MouseOverrideState.foreground_root_window () <> session.host.root_window then
            match release state with
            | Ok() -> ()
            | Error error -> Debug.WriteLine $"RhinosCanFly latched view manipulation: {error}"

let current_mode (state: State) =
    match state.view_latch with
    | NoViewLatch -> None
    | WaitingForRelease pending -> Some pending.mode
    | ViewLatchActive session -> Some session.mode

let is_mode (state: State) (mode: ViewNavigationMode) = current_mode state = Some mode

let start_or_switch (state: State) (host: ViewportHostIdentity) (mode: ViewNavigationMode) (completion: Action option) =
    if state.lifecycle <> Available then
        Error "Mouse button overrides are unavailable."
    else
        match current_mode state with
        | Some current when current = mode -> Ok()
        | None when not (MouseOverrideState.gesture_navigation_engaged state) ->
            match state.routing.prepare_navigation host NavigationTargetPoint.ViewCenter mode with
            | Error error -> Error error
            | Ok prepared -> start state prepared mode completion
        | Some _
        | None ->
            match MouseOverrideState.release_all state with
            | Error error -> Error error
            | Ok() ->
                match state.routing.prepare_navigation host NavigationTargetPoint.ViewCenter mode with
                | Error error -> Error error
                | Ok prepared -> start state prepared mode completion

let stop (state: State) (mode: ViewNavigationMode) =
    if state.lifecycle <> Available then
        Error "Mouse button overrides are unavailable."
    else
        match current_mode state with
        | Some current when current = mode -> MouseOverrideState.release_all state
        | Some _
        | None -> Ok()
