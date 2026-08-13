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

let hook_button_ownership (state: State) (button: SideButton) =
    match button with
    | Mouse4 -> state.side_button_hook_capture.mouse4
    | Mouse5 -> state.side_button_hook_capture.mouse5

let hook_owns_button (state: State) (button: SideButton) =
    hook_button_ownership state button <> NotOwned

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

let exit_key_is_down (state: State) (virtualKey: int) =
    if virtualKey = Win32Native.VK_MBUTTON && state.synthetic_middle <> MiddleReleased then
        state.physical_middle_down
    elif virtualKey = Win32Native.VK_SHIFT && state.synthetic_shift <> ShiftReleased then
        state.physical_shift_keys_down <> 0
    elif virtualKey = Win32Native.VK_LSHIFT && state.synthetic_shift <> ShiftReleased then
        state.physical_shift_keys_down &&& left_shift_bit <> 0
    elif virtualKey = Win32Native.VK_RSHIFT && state.synthetic_shift <> ShiftReleased then
        state.physical_shift_keys_down &&& right_shift_bit <> 0
    elif virtualKey = Win32Native.VK_RBUTTON then
        match state.view_latch with
        | WaitingForRelease _ -> false
        | NoViewLatch
        | RetryingPivot _
        | PivotActive _
        | PanActive _ -> Win32Native.GetAsyncKeyState virtualKey < 0s
    else
        Win32Native.GetAsyncKeyState virtualKey < 0s

let rec exit_keys_down (state: State) (keys: VirtualKey list) =
    match keys with
    | [] -> true
    | VirtualKey key :: remaining -> exit_key_is_down state key && exit_keys_down state remaining

let exit_key_down (state: State) =
    match state.routing.exit with
    | Some binding -> exit_keys_down state binding.virtual_keys
    | None -> false

let rec binding_contains_key (virtualKey: int) (keys: VirtualKey list) =
    match keys with
    | [] -> false
    | VirtualKey key :: remaining -> key = virtualKey || binding_contains_key virtualKey remaining

let exit_binding_contains (state: State) (virtualKey: int) =
    match state.routing.exit with
    | Some binding -> binding_contains_key virtualKey binding.virtual_keys
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

let rec binding_contains_event_key (eventKey: int) (keys: VirtualKey list) =
    match keys with
    | [] -> false
    | key :: remaining -> key_matches_event key eventKey || binding_contains_event_key eventKey remaining

let rec exit_binding_keys_down_for_event (state: State) (eventKey: int) (keys: VirtualKey list) =
    match keys with
    | [] -> true
    | (VirtualKey key as requiredKey) :: remaining ->
        (key_matches_event requiredKey eventKey || exit_key_is_down state key)
        && exit_binding_keys_down_for_event state eventKey remaining

let exit_binding_down_for_event (state: State) (eventKey: int) =
    match state.routing.exit with
    | Some binding ->
        binding_contains_event_key eventKey binding.virtual_keys
        && exit_binding_keys_down_for_event state eventKey binding.virtual_keys
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

    if not (synthetic_input_owned state) then
        state.pending_synthetic_release_root <- ValueNone

    if errors.Count = 0 then
        Ok()
    else
        Error(String.concat "; " errors)

let force_release_synthetic_input (state: State) =
    let errors = ResizeArray<string>()

    if state.synthetic_middle <> MiddleReleased then
        match Win32.send_middle_mouse false with
        | Ok() -> state.synthetic_middle <- MiddleReleased
        | Error error -> errors.Add error

    if state.synthetic_shift <> ShiftReleased then
        match Win32.send_shift_key false with
        | Ok() -> state.synthetic_shift <- ShiftReleased
        | Error error -> errors.Add error

    if not (synthetic_input_owned state) then
        state.pending_synthetic_release_root <- ValueNone

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
    state.poll_timer.Interval <- poll_timer_interval_milliseconds

    if not state.poll_timer.Enabled then
        state.poll_timer.Start()

let keep_watchdog_running (state: State) =
    state.poll_timer.Interval <- poll_timer_watchdog_interval_milliseconds

    if not state.poll_timer.Enabled then
        state.poll_timer.Start()

let fast_poll_required (state: State) =
    state.pending_side_button_events.Count > 0
    || state.navigation_exit_requested
    || state.side_button_restart_pending
    || (state.lifecycle = Available
        && (any_button_engaged state || view_latch_engaged state))

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

let active_navigation_root (state: State) =
    match state.view_latch with
    | WaitingForRelease session
    | RetryingPivot session
    | PanActive session -> ValueSome session.window
    | PivotActive active -> ValueSome active.session.window
    | NoViewLatch ->
        // Keep this boring instead of matching a tuple. This runs on the
        // navigation timer, and the tuple form allocates every time.
        match state.mouse4 with
        | HoldActive window
        | TogglePressed window
        | ToggleLatched window -> ValueSome window
        | Released
        | ToggleReleasePressed ->
            match state.mouse5 with
            | HoldActive window
            | TogglePressed window
            | ToggleLatched window -> ValueSome window
            | Released
            | ToggleReleasePressed -> ValueNone

let navigation_root (state: State) =
    match active_navigation_root state with
    | ValueSome window -> ValueSome window
    | ValueNone -> state.pending_synthetic_release_root

let remember_pending_synthetic_release (state: State) (root: RootWindow voption) =
    if synthetic_input_owned state then
        state.pending_synthetic_release_root <- root
    else
        state.pending_synthetic_release_root <- ValueNone

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

let clear_navigation (state: State) =
    let previousViewLatch = state.view_latch

    state.mouse4 <- Released
    state.mouse5 <- Released
    state.view_latch <- NoViewLatch
    state.side_button_restart_pending <- false
    state.middle_mouse_modifiers_down <- false
    state.navigation_exit_requested <- false
    clear_pending_hook_events state
    previousViewLatch

let finish_navigation_release (state: State) (previousViewLatch: ViewLatch) (releaseResult: Result<unit, string>) =
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

let release_all (state: State) =
    let previousRoot = navigation_root state
    let previousViewLatch = clear_navigation state
    let releaseResult = release_synthetic_input state
    remember_pending_synthetic_release state previousRoot
    finish_navigation_release state previousViewLatch releaseResult

let release_all_after_focus_loss (state: State) =
    let previousRoot = navigation_root state
    let previousViewLatch = clear_navigation state
    let releaseResult = force_release_synthetic_input state
    remember_pending_synthetic_release state previousRoot
    finish_navigation_release state previousViewLatch releaseResult
