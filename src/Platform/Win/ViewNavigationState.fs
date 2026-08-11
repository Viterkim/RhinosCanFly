module RhinosCanFly.Platform.Win.ViewNavigationState

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
    | ShiftPressed ->
        match Win32.send_shift_key false with
        | Ok() ->
            state.synthetic_shift <- ShiftReleased
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

let keep_timer_running (state: State) =
    if not state.poll_timer.Started then
        state.poll_timer.Start()

let stop_timer_if_idle (state: State) =
    if not (any_button_engaged state) && not (view_latch_engaged state) then
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

let release_all (state: State) =
    let previousMouse4 = state.mouse4
    let previousMouse5 = state.mouse5
    let previousViewLatch = state.view_latch
    let hadMiddleMouseDown = middle_mouse_down state
    let restartPending = state.side_button_restart_pending

    state.mouse4 <- Released
    state.mouse5 <- Released
    state.view_latch <- NoViewLatch
    state.side_button_restart_pending <- false

    if not hadMiddleMouseDown || restartPending then
        match release_synthetic_shift state with
        | Ok() ->
            state.middle_mouse_modifiers_down <- false
            state.navigation_exit_requested <- false
            clear_pending_hook_events state
            state.poll_timer.Stop()
            complete_view_latch previousViewLatch
        | Error error ->
            state.mouse4 <- previousMouse4
            state.mouse5 <- previousMouse5
            state.view_latch <- previousViewLatch
            state.side_button_restart_pending <- restartPending
            Error error
    else
        let releaseResult =
            match state.synthetic_shift with
            | ShiftPressed -> Win32.stop_shift_middle_mouse ()
            | ShiftReleased -> Win32.send_middle_mouse false

        match releaseResult with
        | Error error ->
            state.mouse4 <- previousMouse4
            state.mouse5 <- previousMouse5
            state.view_latch <- previousViewLatch
            state.side_button_restart_pending <- restartPending
            Error error
        | Ok() ->
            state.synthetic_shift <- ShiftReleased
            state.middle_mouse_modifiers_down <- false
            state.navigation_exit_requested <- false
            clear_pending_hook_events state
            state.poll_timer.Stop()
            complete_view_latch previousViewLatch
