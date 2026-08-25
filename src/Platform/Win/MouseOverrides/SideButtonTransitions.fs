module RhinosCanFly.Platform.Win.SideButtonTransitions

open RhinosCanFly.Platform.Win.MouseOverrideTypes

let is_down (button: SideButton) =
    let key =
        match button with
        | Middle -> Win32Native.VK_MBUTTON
        | Mouse4 -> Win32Native.VK_XBUTTON1
        | Mouse5 -> Win32Native.VK_XBUTTON2

    Win32Native.GetAsyncKeyState key < 0s

let owner (button: SideButton) =
    match button with
    | Middle -> GestureOwner.Middle
    | Mouse4 -> GestureOwner.Mouse4
    | Mouse5 -> GestureOwner.Mouse5

let process_hook_events (state: State) =
    while state.lifecycle = Available && state.pending_side_button_events.Count > 0 do
        match state.pending_side_button_events.Dequeue() with
        | ButtonDown(button, host, point) when MouseOverrideState.foreground_root_window () = host.root_window ->
            GestureNavigationTransitions.press_or_log
                state
                (owner button)
                (MouseOverrideState.action_for state button)
                host
                point
        | ButtonDown _ -> ()
        | ButtonUp button -> GestureNavigationTransitions.release state (owner button)

    MouseOverrideState.stop_timer_if_idle state

let poll (state: State) = GestureNavigationTransitions.poll state
