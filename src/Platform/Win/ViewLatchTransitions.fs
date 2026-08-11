module RhinosCanFly.Platform.Win.ViewLatchTransitions

open System
open System.Diagnostics
open RhinosCanFly
open RhinosCanFly.Platform.Win.ViewNavigationTypes

let release (state: State) =
    match state.view_latch with
    | NoViewLatch -> Ok()
    | (WaitingForRelease _ | RetryingPivot _) as inactive ->
        state.view_latch <- NoViewLatch
        ViewNavigationState.stop_timer_if_idle state
        ViewNavigationState.complete_view_latch inactive
    | (PivotActive _ | PanActive _) as active ->
        if ViewNavigationState.any_button_holds_middle state then
            ViewNavigationState.release_all state
        else
            state.view_latch <- NoViewLatch

            let releaseResult =
                match active, state.synthetic_shift with
                | PanActive _, ShiftPressed -> Win32.stop_shift_middle_mouse ()
                | _ -> Win32.send_middle_mouse false

            match releaseResult with
            | Ok() ->
                state.synthetic_shift <- ShiftReleased
                ViewNavigationState.stop_timer_if_idle state
                ViewNavigationState.complete_view_latch active
            | Error error ->
                state.view_latch <- active
                Error error

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

    match shiftRoute, altRoute with
    | Some Pan, _
    | _, Some Pan -> Some Pan
    | Some Pivot, _
    | _, Some Pivot -> Some Pivot
    | None, None -> None

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
        match ViewNavigationState.release_all state with
        | Ok() -> Ok true
        | Error error -> Error error
    else
        match state.view_latch with
        | PivotActive _
        | PanActive _
        | WaitingForRelease _
        | RetryingPivot _ ->
            match release state with
            | Ok() -> Ok true
            | Error error -> Error error
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
                              completion = None }

                    ViewNavigationState.keep_timer_running state
                    Ok true
            | Some _
            | None -> Ok false

let right_click_enabled (state: State) =
    ViewNavigationState.right_mouse_exit_enabled state
    || Option.isSome state.routing.shift_right_click
    || Option.isSome state.routing.alt_right_click

let activate (state: State) (session: ViewLatchSession) =
    if ViewNavigationState.any_button_holds_middle state then
        Error "Another mouse override already owns the middle mouse button."
    else
        let result =
            match session.mode with
            | Pivot -> Win32.send_middle_mouse true
            | Pan -> Win32.start_shift_middle_mouse ()

        match result with
        | Ok() ->
            state.synthetic_shift <-
                match session.mode with
                | Pivot -> ShiftReleased
                | Pan -> ShiftPressed

            state.view_latch <-
                match session.mode with
                | Pivot ->
                    PivotActive
                        { session = session
                          modifiers_down = false }
                | Pan -> PanActive session

            ViewNavigationState.keep_timer_running state
            Ok()
        | Error error ->
            state.view_latch <- NoViewLatch
            ViewNavigationState.stop_timer_if_idle state
            Error error

let update (state: State) =
    match state.view_latch with
    | NoViewLatch -> ()
    | WaitingForRelease pending ->
        if ViewNavigationState.foreground_root_window () <> pending.window then
            state.view_latch <- NoViewLatch
            ViewNavigationState.stop_timer_if_idle state
            complete_or_log (WaitingForRelease pending)
        elif input_released () then
            match activate state pending with
            | Ok() -> ()
            | Error error ->
                complete_or_log (WaitingForRelease pending)
                Debug.WriteLine $"RhinosCanFly latched view manipulation: {error}"
    | RetryingPivot session ->
        if ViewNavigationState.foreground_root_window () <> session.window then
            state.view_latch <- NoViewLatch
            ViewNavigationState.stop_timer_if_idle state
            complete_or_log (RetryingPivot session)
        elif ViewNavigationState.any_button_holds_middle state then
            match ViewNavigationState.release_all state with
            | Ok() -> ()
            | Error error -> Debug.WriteLine $"RhinosCanFly latched view manipulation: {error}"
        else
            let modifiersDown = ViewNavigationState.view_modifier_down ()

            match Win32.send_middle_mouse true with
            | Ok() ->
                state.view_latch <-
                    PivotActive
                        { session = session
                          modifiers_down = modifiersDown }
            | Error error ->
                state.view_latch <- NoViewLatch
                ViewNavigationState.stop_timer_if_idle state
                complete_or_log (RetryingPivot session)
                Debug.WriteLine $"RhinosCanFly latched view manipulation: {error}"
    | PivotActive active ->
        if ViewNavigationState.foreground_root_window () <> active.session.window then
            match release state with
            | Ok() -> ()
            | Error error -> Debug.WriteLine $"RhinosCanFly latched view manipulation: {error}"
        else
            let modifiersDown = ViewNavigationState.view_modifier_down ()

            if
                modifiersDown <> active.modifiers_down
                && not (ViewNavigationState.any_button_holds_middle state)
            then
                match Win32.send_middle_mouse false with
                | Error error -> Debug.WriteLine $"RhinosCanFly latched view manipulation: {error}"
                | Ok() -> state.view_latch <- RetryingPivot active.session
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
    | RetryingPivot session
    | PanActive session -> Some session.mode
    | PivotActive active -> Some active.session.mode

let is_mode (state: State) (mode: ViewLatchMode) = current_mode state = Some mode

let start (state: State) (window: RootWindow) (mode: ViewLatchMode) (completion: Action option) =
    state.view_latch <-
        WaitingForRelease
            { window = window
              mode = mode
              completion = completion }

    ViewNavigationState.keep_timer_running state
    Ok()

let start_or_switch (state: State) (window: RootWindow) (mode: ViewLatchMode) (completion: Action option) =
    if state.lifecycle <> Available then
        Error "Mouse button overrides are unavailable."
    else
        match current_mode state with
        | Some current when current = mode -> Ok()
        | None when not (ViewNavigationState.any_button_holds_middle state) -> start state window mode completion
        | Some _
        | None ->
            match ViewNavigationState.release_all state with
            | Error error -> Error error
            | Ok() -> start state window mode completion

let stop (state: State) (mode: ViewLatchMode) =
    if state.lifecycle <> Available then
        Error "Mouse button overrides are unavailable."
    else
        match current_mode state with
        | Some current when current = mode -> ViewNavigationState.release_all state
        | Some _
        | None -> Ok()
