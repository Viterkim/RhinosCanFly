module RhinosCanFly.Platform.Win.ViewLatchTransitions

open System
open System.Diagnostics
open RhinosCanFly
open RhinosCanFly.Platform.Win.ViewNavigationTypes

let release (state: State) =
    match state.view_latch with
    | NoViewLatch -> Ok()
    | (WaitingForRelease _ | PivotActive _ | PanActive _) as active ->
        state.view_latch <- NoViewLatch
        ViewNavigationState.stop_timer_if_idle state
        ViewNavigationState.complete_view_latch active

let complete_or_log (latch: ViewLatch) =
    match ViewNavigationState.complete_view_latch latch with
    | Ok() -> ()
    | Error error -> Debug.WriteLine $"RhinosCanFly latched view manipulation: {error}"

let requested_mode (state: State) =
    let shiftRoute =
        if ViewNavigationState.shift_down () then
            state.routing.shift_right_click
        else
            None

    let altRoute =
        if ViewNavigationState.alt_down () then
            state.routing.alt_right_click
        else
            None

    if shiftRoute = Some Pan || altRoute = Some Pan then
        Some Pan
    elif shiftRoute = Some Pivot || altRoute = Some Pivot then
        Some Pivot
    else
        None

let input_released () =
    Win32Native.GetAsyncKeyState Win32Native.VK_RBUTTON >= 0s
    && not (ViewNavigationState.shift_down ())
    && not (ViewNavigationState.alt_down ())

let handle_right_click (state: State) (window: RootWindow) =
    if
        ViewNavigationState.right_mouse_exit_enabled state
        && (state.routing.exit_on_mouse_right || ViewNavigationState.exit_key_down state)
        && ViewNavigationState.any_button_engaged state
    then
        state.navigation_exit_requested <- true
        ViewNavigationState.keep_timer_running state
        Ok true
    else
        match state.view_latch with
        | PivotActive _
        | PanActive _
        | WaitingForRelease _ ->
            state.navigation_exit_requested <- true
            ViewNavigationState.keep_timer_running state
            Ok true
        | NoViewLatch ->
            match requested_mode state with
            | Some mode when state.lifecycle = Available ->
                let released =
                    if ViewNavigationState.any_button_engaged state then
                        ViewNavigationState.release_all state
                    else
                        Ok()

                match released with
                | Error error -> Error error
                | Ok() ->
                    state.view_latch <-
                        WaitingForRelease
                            { window = window
                              mode = mode
                              started_at = Stopwatch.GetTimestamp()
                              completion = None }

                    ViewNavigationState.keep_timer_running state
                    Ok true
            | Some _
            | None -> Ok false

let activate (state: State) (session: ViewLatchSession) =
    if ViewNavigationState.side_button_navigation_active state then
        Error "Another view navigation mode is already active."
    else
        state.view_latch <-
            match session.mode with
            | Pivot -> PivotActive session
            | Pan -> PanActive session

        ViewNavigationState.keep_timer_running state
        Ok()

let update (state: State) =
    match state.view_latch with
    | NoViewLatch -> ()
    | WaitingForRelease pending ->
        let timedOut = ViewNavigationState.transition_timed_out pending

        if ViewNavigationState.foreground_root_window () <> pending.window || timedOut then
            state.view_latch <- NoViewLatch
            ViewNavigationState.stop_timer_if_idle state
            complete_or_log (WaitingForRelease pending)
        elif input_released () then
            match activate state pending with
            | Ok() -> ()
            | Error error ->
                complete_or_log (WaitingForRelease pending)
                Debug.WriteLine $"RhinosCanFly latched view manipulation: {error}"
    | PivotActive session ->
        if ViewNavigationState.foreground_root_window () <> session.window then
            match release state with
            | Ok() -> ()
            | Error error -> Debug.WriteLine $"RhinosCanFly latched view manipulation: {error}"
    | PanActive session ->
        if ViewNavigationState.foreground_root_window () <> session.window then
            match release state with
            | Ok() -> ()
            | Error error -> Debug.WriteLine $"RhinosCanFly latched view manipulation: {error}"

let configured_mode (mode: ModifiedRightClickMode) =
    match mode with
    | ModifiedRightClickMode.Off -> None
    | ModifiedRightClickMode.Pivot -> Some Pivot
    | ModifiedRightClickMode.Pan -> Some Pan
    | _ -> None

let current_mode (state: State) =
    match state.view_latch with
    | NoViewLatch -> None
    | WaitingForRelease pending -> Some pending.mode
    | PivotActive session
    | PanActive session -> Some session.mode

let is_mode (state: State) (mode: ViewNavigationMode) = current_mode state = Some mode

let start (state: State) (window: RootWindow) (mode: ViewNavigationMode) (completion: Action option) =
    state.view_latch <-
        WaitingForRelease
            { window = window
              mode = mode
              started_at = Stopwatch.GetTimestamp()
              completion = completion }

    ViewNavigationState.keep_timer_running state
    Ok()

let start_or_switch (state: State) (window: RootWindow) (mode: ViewNavigationMode) (completion: Action option) =
    if state.lifecycle <> Available then
        Error "Mouse button overrides are unavailable."
    else
        match current_mode state with
        | Some current when current = mode -> Ok()
        | None when not (ViewNavigationState.side_button_navigation_active state) -> start state window mode completion
        | Some _
        | None ->
            match ViewNavigationState.release_all state with
            | Error error -> Error error
            | Ok() -> start state window mode completion

let stop (state: State) (mode: ViewNavigationMode) =
    if state.lifecycle <> Available then
        Error "Mouse button overrides are unavailable."
    else
        match current_mode state with
        | Some current when current = mode -> ViewNavigationState.release_all state
        | Some _
        | None -> Ok()
