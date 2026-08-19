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
    match hook_button_ownership state button with
    | NotOwned -> false
    | Owned
    | ReleaseObserved -> true

let set_hook_button_ownership (state: State) (button: SideButton) (ownership: HookButtonOwnership) =
    match button with
    | Mouse4 -> state.side_button_hook_capture.mouse4 <- ownership
    | Mouse5 -> state.side_button_hook_capture.mouse5 <- ownership

let observe_hook_button_released (state: State) (button: SideButton) =
    match hook_button_ownership state button with
    | NotOwned -> ()
    | Owned ->
        match button with
        | Mouse4 -> state.side_button_hook_capture.mouse4 <- ReleaseObserved
        | Mouse5 -> state.side_button_hook_capture.mouse5 <- ReleaseObserved
    | ReleaseObserved -> set_hook_button_ownership state button NotOwned

let hook_owns_any_button (state: State) =
    hook_owns_button state Mouse4 || hook_owns_button state Mouse5

let mode_for (state: State) (button: SideButton) =
    match button with
    | Mouse4 -> state.routing.mouse4
    | Mouse5 -> state.routing.mouse5

let side_button_routing_enabled (state: State) =
    match state.routing.mouse4 with
    | MouseButtonPivotMode.Hold
    | MouseButtonPivotMode.Toggle -> true
    | MouseButtonPivotMode.Off
    | _ ->
        match state.routing.mouse5 with
        | MouseButtonPivotMode.Hold
        | MouseButtonPivotMode.Toggle -> true
        | MouseButtonPivotMode.Off
        | _ -> false

let button_navigation_active (buttonState: SideButtonState) =
    match buttonState with
    | HoldActive _
    | TogglePressed _
    | ToggleLatched _ -> true
    | Released
    | ToggleReleasePressed -> false

let side_button_navigation_active (state: State) =
    button_navigation_active state.mouse4 || button_navigation_active state.mouse5

let any_button_engaged (state: State) =
    match state.mouse4 with
    | HoldActive _
    | TogglePressed _
    | ToggleLatched _
    | ToggleReleasePressed -> true
    | Released ->
        match state.mouse5 with
        | HoldActive _
        | TogglePressed _
        | ToggleLatched _
        | ToggleReleasePressed -> true
        | Released -> false

let view_latch_engaged (state: State) =
    match state.view_latch with
    | NoViewLatch -> false
    | WaitingForRelease _
    | PivotActive _
    | PanActive _ -> true

let exit_key_is_down (state: State) (virtualKey: int) =
    if virtualKey = Win32Native.VK_RBUTTON then
        match state.view_latch with
        | WaitingForRelease _ -> false
        | NoViewLatch
        | PivotActive _
        | PanActive _ -> Win32Native.GetAsyncKeyState virtualKey < 0s
    else
        Win32Native.GetAsyncKeyState virtualKey < 0s

let exit_keys_down (state: State) (keys: VirtualKey array) =
    let mutable index = 0
    let mutable down = keys.Length > 0

    while down && index < keys.Length do
        let (VirtualKey key) = keys[index]
        down <- exit_key_is_down state key
        index <- index + 1

    down

let exit_key_down (state: State) =
    match state.routing.exit with
    | Some binding -> exit_keys_down state binding.virtual_keys
    | None -> false

let binding_contains_key (virtualKey: int) (keys: VirtualKey array) =
    let mutable index = 0
    let mutable found = false

    while not found && index < keys.Length do
        let (VirtualKey key) = keys[index]
        found <- key = virtualKey
        index <- index + 1

    found

let exit_binding_contains (state: State) (virtualKey: int) =
    match state.routing.exit with
    | Some binding -> binding_contains_key virtualKey binding.virtual_keys
    | None -> false

let right_mouse_exit_enabled (state: State) =
    state.routing.exit_on_mouse_right
    || exit_binding_contains state Win32Native.VK_RBUTTON

let shift_down () =
    Win32Native.GetAsyncKeyState Win32Native.VK_SHIFT < 0s
    || Win32Native.GetAsyncKeyState Win32Native.VK_LSHIFT < 0s
    || Win32Native.GetAsyncKeyState Win32Native.VK_RSHIFT < 0s

let alt_down () =
    Win32Native.GetAsyncKeyState Win32Native.VK_MENU < 0s
    || Win32Native.GetAsyncKeyState Win32Native.VK_LMENU < 0s
    || Win32Native.GetAsyncKeyState Win32Native.VK_RMENU < 0s

let transition_timed_out (session: ViewLatchSession) =
    let elapsedTicks = Stopwatch.GetTimestamp() - session.started_at
    float elapsedTicks / float Stopwatch.Frequency >= TRANSITION_TIMEOUT_SECONDS

let keep_timer_running (state: State) =
    state.poll_timer.Interval <- POLL_TIMER_INTERVAL_MILLISECONDS

    if not state.poll_timer.Enabled then
        state.poll_timer.Start()

let keep_watchdog_running (state: State) =
    state.poll_timer.Interval <- POLL_TIMER_WATCHDOG_INTERVAL_MILLISECONDS

    if not state.poll_timer.Enabled then
        state.poll_timer.Start()

let fast_poll_required (state: State) =
    state.pending_side_button_events.Count > 0
    || state.navigation_exit_requested
    || (state.lifecycle = Available
        && (any_button_engaged state || view_latch_engaged state))

let stop_timer_if_idle (state: State) =
    if not (any_button_engaged state) && not (view_latch_engaged state) then
        state.poll_timer.Stop()

let root_window (window: nativeint) =
    let root = Win32Native.GetAncestor(window, Win32Native.GA_ROOT)

    RootWindow(if root = nativeint 0 then window else root)

let foreground_root_window () =
    RootWindow(Win32Native.GetForegroundWindow())

let try_bring_root_window_to_foreground (window: RootWindow) =
    if foreground_root_window () = window then
        true
    else
        let (RootWindow handle) = window

        handle <> nativeint 0
        && Win32Native.IsWindow handle
        && Win32Native.IsWindowEnabled handle
        && Win32Native.SetForegroundWindow handle
        && foreground_root_window () = window

let same_host (left: ViewportHostIdentity) (right: ViewportHostIdentity) =
    left.view_serial_number = right.view_serial_number
    && left.document_serial_number = right.document_serial_number
    && left.view_window = right.view_window
    && left.root_window = right.root_window

let navigation_host (state: State) =
    match state.view_latch with
    | WaitingForRelease session
    | PivotActive session
    | PanActive session -> ValueSome session.host
    | NoViewLatch ->
        // Keep this boring instead of matching a tuple. This runs on the
        // navigation timer, and the tuple form allocates every time.
        match state.mouse4 with
        | HoldActive host
        | TogglePressed host
        | ToggleLatched host -> ValueSome host
        | Released
        | ToggleReleasePressed ->
            match state.mouse5 with
            | HoldActive host
            | TogglePressed host
            | ToggleLatched host -> ValueSome host
            | Released
            | ToggleReleasePressed -> ValueNone

let view_latch_completion (latch: ViewLatch) =
    match latch with
    | NoViewLatch -> None
    | WaitingForRelease session
    | PivotActive session
    | PanActive session -> session.completion

let complete_view_latch (latch: ViewLatch) =
    match view_latch_completion latch with
    | None -> Ok()
    | Some completion ->
        try
            completion.Invoke()
            Ok()
        with error ->
            Error $"Could not restore the original view: {error.Message}"

let clear_navigation (state: State) =
    let previousViewLatch = state.view_latch

    state.mouse4 <- Released
    state.mouse5 <- Released
    state.view_latch <- NoViewLatch
    state.navigation_exit_requested <- false
    state.pending_side_button_events.Clear()
    previousViewLatch

let release_all (state: State) =
    let previousViewLatch = clear_navigation state
    state.poll_timer.Stop()
    complete_view_latch previousViewLatch
