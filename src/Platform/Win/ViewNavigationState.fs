module RhinosCanFly.Platform.Win.ViewNavigationState

open System.Diagnostics
open RhinosCanFly
open RhinosCanFly.Platform.Win.ViewNavigationTypes

let get_button_state (state: State) (button: SideButton) =
    match button with
    | Mouse4 -> state.mouse4
    | Mouse5 -> state.mouse5

let set_button_state (state: State) (button: SideButton) (buttonState: SideButtonState) =
    match button with
    | Mouse4 -> state.mouse4 <- buttonState
    | Mouse5 -> state.mouse5 <- buttonState

let hook_owns_button (state: State) (button: SideButton) =
    match button with
    | Mouse4 -> state.side_button_hook_capture.mouse4 <> NotOwned
    | Mouse5 -> state.side_button_hook_capture.mouse5 <> NotOwned

let set_hook_owns_button (state: State) (button: SideButton) (owned: bool) =
    let ownership = if owned then Owned else NotOwned

    match button with
    | Mouse4 -> state.side_button_hook_capture.mouse4 <- ownership
    | Mouse5 -> state.side_button_hook_capture.mouse5 <- ownership

let observe_hook_button_released (state: State) (button: SideButton) =
    let ownership =
        match button with
        | Mouse4 -> state.side_button_hook_capture.mouse4
        | Mouse5 -> state.side_button_hook_capture.mouse5

    match ownership with
    | NotOwned -> ()
    | Owned ->
        match button with
        | Mouse4 -> state.side_button_hook_capture.mouse4 <- ReleaseObserved
        | Mouse5 -> state.side_button_hook_capture.mouse5 <- ReleaseObserved
    | ReleaseObserved -> set_hook_owns_button state button false

let clear_pending_hook_events (state: State) =
    state.pending_side_button_events.Clear()

let hook_owns_any_button (state: State) =
    state.side_button_hook_capture.mouse4 <> NotOwned
    || state.side_button_hook_capture.mouse5 <> NotOwned

let mode_for (state: State) (button: SideButton) =
    match button with
    | Mouse4 -> state.routing.mouse4
    | Mouse5 -> state.routing.mouse5

let side_button_routing_enabled (state: State) =
    state.routing.mouse4 <> Disabled || state.routing.mouse5 <> Disabled

let button_holds_middle (buttonState: SideButtonState) =
    match buttonState with
    | HoldActive _
    | TogglePressed _
    | ToggleLatched _ -> true
    | Released
    | ToggleReleasePressed -> false

let any_button_holds_middle (state: State) =
    button_holds_middle state.mouse4 || button_holds_middle state.mouse5

let any_button_engaged (state: State) =
    state.mouse4 <> Released || state.mouse5 <> Released

let view_latch_active (state: State) =
    match state.view_latch with
    | PivotActive _
    | PanActive _ -> true
    | NoViewLatch
    | WaitingForRelease _
    | RetryingPivot _ -> false

let view_latch_engaged (state: State) =
    match state.view_latch with
    | NoViewLatch -> false
    | WaitingForRelease _
    | RetryingPivot _
    | PivotActive _
    | PanActive _ -> true

let middle_mouse_down (state: State) =
    any_button_holds_middle state || view_latch_active state

let synthetic_input_owned (state: State) =
    state.synthetic_middle <> MiddleReleased
    || state.synthetic_shift <> ShiftReleased

let synthetic_shift_owned (state: State) = state.synthetic_shift <> ShiftReleased

[<Literal>]
let left_shift_bit = 1

[<Literal>]
let right_shift_bit = 2

let shift_bit (virtualKey: int) =
    if virtualKey = Win32Native.VK_RSHIFT then
        right_shift_bit
    else
        left_shift_bit

let observe_physical_shift (state: State) (virtualKey: int) (released: bool) =
    let bit = shift_bit virtualKey

    if released then
        state.physical_shift_keys_down <- state.physical_shift_keys_down &&& (~~~bit)
    else
        state.physical_shift_keys_down <- state.physical_shift_keys_down ||| bit

let observe_physical_middle (state: State) (down: bool) = state.physical_middle_down <- down

let reconcile_release_pending_physical_input (state: State) =
    if state.synthetic_shift = ShiftReleasePending then
        let mutable physicalShiftKeys = 0

        if Win32Native.GetAsyncKeyState Win32Native.VK_LSHIFT < 0s then
            physicalShiftKeys <- physicalShiftKeys ||| left_shift_bit

        if Win32Native.GetAsyncKeyState Win32Native.VK_RSHIFT < 0s then
            physicalShiftKeys <- physicalShiftKeys ||| right_shift_bit

        state.physical_shift_keys_down <- physicalShiftKeys

    if state.synthetic_middle = MiddleReleasePending then
        state.physical_middle_down <- Win32Native.GetAsyncKeyState Win32Native.VK_MBUTTON < 0s

let exit_key_down (state: State) =
    match state.routing.exit with
    | Some binding -> PlatformBindings.is_down binding
    | None -> false

let exit_binding_contains (state: State) (virtualKey: int) =
    match state.routing.exit with
    | Some binding ->
        binding.virtual_keys
        |> List.exists (fun (configuredKey: VirtualKey) ->
            let (VirtualKey key) = configuredKey
            key = virtualKey)
    | None -> false

let key_matches_event (key: VirtualKey) (eventKey: int) =
    let (VirtualKey requiredKey) = key

    requiredKey = eventKey
    || (requiredKey = Win32Native.VK_SHIFT
        && (eventKey = Win32Native.VK_LSHIFT || eventKey = Win32Native.VK_RSHIFT))
    || (requiredKey = Win32Native.VK_CONTROL
        && (eventKey = Win32Native.VK_LCONTROL || eventKey = Win32Native.VK_RCONTROL))
    || (requiredKey = Win32Native.VK_MENU
        && (eventKey = Win32Native.VK_LMENU || eventKey = Win32Native.VK_RMENU))

let exit_binding_down_for_event (state: State) (eventKey: int) =
    match state.routing.exit with
    | Some binding when
        binding.virtual_keys
        |> List.exists (fun (key: VirtualKey) -> key_matches_event key eventKey)
        ->
        binding.virtual_keys
        |> List.forall (fun (requiredKey: VirtualKey) ->
            let (VirtualKey key) = requiredKey
            key_matches_event requiredKey eventKey || Win32Native.GetAsyncKeyState key < 0s)
    | Some _
    | None -> false

let left_mouse_exit_enabled (state: State) =
    state.routing.exit_on_mouse_left
    || exit_binding_contains state Win32Native.VK_LBUTTON

let right_mouse_exit_enabled (state: State) =
    state.routing.exit_on_mouse_right
    || exit_binding_contains state Win32Native.VK_RBUTTON

let release_synthetic_shift (state: State) =
    match state.synthetic_shift with
    | ShiftReleased -> Ok()
    | ShiftPressed
    | ShiftReleasePending when state.physical_shift_keys_down <> 0 ->
        state.synthetic_shift <- ShiftReleasePending
        Error "Synthetic Shift is waiting for the physical Shift key to be released."
    | ShiftPressed
    | ShiftReleasePending ->
        match Win32.send_shift_key false with
        | Ok() ->
            state.synthetic_shift <- ShiftReleased
            InputDiagnostics.record InputDiagnostics.EventKind.SyntheticShiftUp 0L 0L
            Ok()
        | Error error -> Error error

let release_synthetic_middle (state: State) =
    match state.synthetic_middle with
    | MiddleReleased -> Ok()
    | MiddlePressed
    | MiddleReleasePending when state.physical_middle_down ->
        state.synthetic_middle <- MiddleReleasePending
        Error "Synthetic middle mouse is waiting for the physical middle button to be released."
    | MiddlePressed
    | MiddleReleasePending ->
        match Win32.send_middle_mouse false with
        | Ok() ->
            state.synthetic_middle <- MiddleReleased
            InputDiagnostics.record InputDiagnostics.EventKind.SyntheticMiddleUp 0L 0L
            Ok()
        | Error error -> Error error

let release_synthetic_input (state: State) =
    let errors = ResizeArray<string>()

    match release_synthetic_middle state with
    | Ok() -> ()
    | Error error -> errors.Add error

    match release_synthetic_shift state with
    | Ok() -> ()
    | Error error -> errors.Add error

    if errors.Count = 0 then
        Ok()
    else
        Error(String.concat "; " errors)

let press_synthetic_middle (state: State) =
    if state.synthetic_middle <> MiddleReleased then
        Ok()
    elif state.synthetic_shift <> ShiftReleased then
        Error "Another mouse override already owns synthetic Shift."
    elif Win32Native.GetAsyncKeyState Win32Native.VK_MBUTTON < 0s then
        Error "The physical middle mouse button is already down."
    else
        state.physical_middle_down <- false

        match Win32.send_middle_mouse true with
        | Ok() ->
            state.synthetic_middle <- MiddlePressed
            InputDiagnostics.record InputDiagnostics.EventKind.SyntheticMiddleDown 0L 0L
            Ok()
        | Error error -> Error error

let shift_down () =
    Win32Native.GetAsyncKeyState Win32Native.VK_SHIFT < 0s
    || Win32Native.GetAsyncKeyState Win32Native.VK_LSHIFT < 0s
    || Win32Native.GetAsyncKeyState Win32Native.VK_RSHIFT < 0s

let alt_down () =
    Win32Native.GetAsyncKeyState Win32Native.VK_MENU < 0s
    || Win32Native.GetAsyncKeyState Win32Native.VK_LMENU < 0s
    || Win32Native.GetAsyncKeyState Win32Native.VK_RMENU < 0s

let view_modifier_down () = shift_down () || alt_down ()

let transition_timed_out (session: ViewLatchSession) =
    let elapsedTicks = Stopwatch.GetTimestamp() - session.started_at
    float elapsedTicks / float Stopwatch.Frequency >= transition_timeout_seconds

let press_synthetic_shift_middle (state: State) =
    if synthetic_input_owned state then
        Error "Another mouse override already owns synthetic input."
    elif Win32Native.GetAsyncKeyState Win32Native.VK_MBUTTON < 0s then
        Error "The physical middle mouse button is already down."
    elif view_modifier_down () then
        Error "Release Shift and Alt before starting pan."
    else
        state.physical_shift_keys_down <- 0
        state.physical_middle_down <- false

        match
            Win32.try_send_inputs
                "SendInput(shift + middle mouse)"
                [| Win32.keyboard_input Win32Native.VK_SHIFT 0u
                   Win32.mouse_input Win32Native.MOUSEEVENTF_MIDDLEDOWN |]
        with
        | Ok() ->
            state.synthetic_shift <- ShiftPressed
            state.synthetic_middle <- MiddlePressed
            InputDiagnostics.record InputDiagnostics.EventKind.SyntheticShiftDown 0L 0L
            InputDiagnostics.record InputDiagnostics.EventKind.SyntheticMiddleDown 0L 0L
            Ok()
        | Error(struct (sent, error)) ->
            if sent >= 1u then
                state.synthetic_shift <- ShiftPressed

            if sent >= 2u then
                state.synthetic_middle <- MiddlePressed

            match release_synthetic_input state with
            | Ok() -> Error error
            | Error cleanupError -> Error $"{error}; cleanup failed: {cleanupError}"

let keep_timer_running (state: State) =
    let changed =
        not state.poll_timer.Started
        || state.poll_timer.Interval <> poll_timer_interval_seconds

    state.poll_timer.Interval <- poll_timer_interval_seconds

    if not state.poll_timer.Started then
        state.poll_timer.Start()

    if changed then
        InputDiagnostics.record InputDiagnostics.EventKind.TimerFast 0L 0L

let keep_watchdog_running (state: State) =
    let changed =
        not state.poll_timer.Started
        || state.poll_timer.Interval <> poll_timer_watchdog_interval_seconds

    state.poll_timer.Interval <- poll_timer_watchdog_interval_seconds

    if not state.poll_timer.Started then
        state.poll_timer.Start()

    if changed then
        InputDiagnostics.record InputDiagnostics.EventKind.TimerWatchdog 0L 0L

let fast_poll_required (state: State) =
    state.pending_side_button_events.Count > 0
    || state.navigation_exit_requested
    || state.side_button_restart_pending
    || match state.view_latch with
       | WaitingForRelease _
       | RetryingPivot _ -> true
       | NoViewLatch
       | PivotActive _
       | PanActive _ -> false

let stop_timer_if_idle (state: State) =
    if
        not (any_button_engaged state)
        && not (view_latch_engaged state)
        && not (synthetic_input_owned state)
    then
        state.poll_timer.Stop()

let root_window (window: nativeint) =
    let root = Win32Native.GetAncestor(window, Win32Native.GA_ROOT)

    RootWindow(if root = nativeint 0 then window else root)

let foreground_root_window () =
    RootWindow(Win32Native.GetForegroundWindow())

let view_latch_completion (latch: ViewLatch) =
    match latch with
    | NoViewLatch -> None
    | WaitingForRelease session
    | RetryingPivot session
    | PanActive session -> session.completion
    | PivotActive active -> active.session.completion

let complete_view_latch (latch: ViewLatch) =
    match view_latch_completion latch with
    | None -> Ok()
    | Some completion ->
        try
            completion.Invoke()
            Ok()
        with error ->
            Error $"Could not restore the original view: {error.Message}"

let defer_view_latch_completion (state: State) (latch: ViewLatch) =
    match view_latch_completion latch with
    | Some completion when Option.isNone state.pending_view_completion ->
        state.pending_view_completion <- Some completion
    | Some _
    | None -> ()

let complete_pending_view_latch (state: State) =
    match state.pending_view_completion with
    | None -> Ok()
    | Some completion ->
        state.pending_view_completion <- None

        try
            completion.Invoke()
            Ok()
        with error ->
            Error $"Could not restore the original view: {error.Message}"

let complete_view_latch_after_input_release (state: State) (latch: ViewLatch) =
    if synthetic_input_owned state then
        defer_view_latch_completion state latch
        Ok()
    else
        complete_view_latch latch

let release_all (state: State) =
    let previousViewLatch = state.view_latch

    state.mouse4 <- Released
    state.mouse5 <- Released
    state.view_latch <- NoViewLatch
    state.side_button_restart_pending <- false
    state.middle_mouse_modifiers_down <- false
    state.navigation_exit_requested <- false
    clear_pending_hook_events state

    let releaseResult = release_synthetic_input state

    let completionResult =
        complete_view_latch_after_input_release state previousViewLatch

    let pendingCompletionResult =
        if synthetic_input_owned state then
            Ok()
        else
            complete_pending_view_latch state

    if synthetic_input_owned state then
        keep_watchdog_running state
    else
        state.poll_timer.Stop()

    let errors = ResizeArray<string>()

    match releaseResult with
    | Ok() -> ()
    | Error error -> errors.Add error

    match completionResult with
    | Ok() -> ()
    | Error error -> errors.Add error

    match pendingCompletionResult with
    | Ok() -> ()
    | Error error -> errors.Add error

    if errors.Count = 0 then
        Ok()
    else
        Error(String.concat "; " errors)
