module RhinosCanFly.Platform.Win.SideButtonTransitions

open RhinosCanFly.Platform.Win.ViewNavigationTypes

let is_down (button: SideButton) =
    let key =
        match button with
        | Mouse4 -> Win32Native.VK_XBUTTON1
        | Mouse5 -> Win32Native.VK_XBUTTON2

    Win32Native.GetAsyncKeyState key < 0s

let owner (button: SideButton) =
    match button with
    | Mouse4 -> GestureOwner.Mouse4
    | Mouse5 -> GestureOwner.Mouse5

let process_hook_events (state: State) =
    while state.lifecycle = Available && state.pending_side_button_events.Count > 0 do
        match state.pending_side_button_events.Dequeue() with
        | ButtonDown(button, host, point) when ViewNavigationState.foreground_root_window () = host.root_window ->
            GestureNavigationTransitions.press_or_log
                state
                (owner button)
                (ViewNavigationState.action_for state button)
                host
                point
        | ButtonDown _ -> ()
        | ButtonUp button -> GestureNavigationTransitions.release state (owner button)

    ViewNavigationState.stop_timer_if_idle state

let poll (state: State) = GestureNavigationTransitions.poll state
