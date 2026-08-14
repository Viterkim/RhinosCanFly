module RhinosCanFly.Platform.Win.SideButtonTransitions

open System.Diagnostics
open RhinosCanFly
open RhinosCanFly.Platform.Win.ViewNavigationTypes

let is_down (button: SideButton) =
    let key =
        match button with
        | Mouse4 -> Win32Native.VK_XBUTTON1
        | Mouse5 -> Win32Native.VK_XBUTTON2

    Win32Native.GetAsyncKeyState key < 0s

let begin_hold (state: State) (button: SideButton) (window: RootWindow) =
    ViewNavigationState.set_button_state state button (HoldActive window)
    ViewNavigationState.keep_timer_running state

let finish (state: State) (button: SideButton) =
    match ViewNavigationState.get_button_state state button with
    | Released -> ()
    | TogglePressed window -> ViewNavigationState.set_button_state state button (ToggleLatched window)
    | ToggleLatched _ -> ()
    | ToggleReleasePressed -> ViewNavigationState.set_button_state state button Released
    | HoldActive _ -> ViewNavigationState.set_button_state state button Released

    ViewNavigationState.stop_timer_if_idle state

let stop_toggle (state: State) (button: SideButton) (nextState: SideButtonState) =
    ViewNavigationState.set_button_state state button nextState
    ViewNavigationState.stop_timer_if_idle state

let toggle (state: State) (button: SideButton) (window: RootWindow) =
    match ViewNavigationState.get_button_state state button with
    | Released ->
        ViewNavigationState.set_button_state state button (TogglePressed window)
        ViewNavigationState.keep_timer_running state
    | ToggleLatched _ -> stop_toggle state button ToggleReleasePressed
    | HoldActive _
    | TogglePressed _
    | ToggleReleasePressed -> ()

let handle_down (state: State) (button: SideButton) (window: RootWindow) =
    let mode = ViewNavigationState.mode_for state button

    if ViewNavigationState.view_latch_engaged state then
        match ViewLatchTransitions.release state with
        | Error error -> Debug.WriteLine $"RhinosCanFly mouse override: {error}"
        | Ok() ->
            match mode with
            | Disabled -> ()
            | Hold -> begin_hold state button window
            | Toggle ->
                ViewNavigationState.set_button_state state button ToggleReleasePressed
                ViewNavigationState.keep_timer_running state
    else
        match mode with
        | Disabled -> ()
        | Hold -> begin_hold state button window
        | Toggle -> toggle state button window

let process_hook_events (state: State) =
    while state.lifecycle = Available && state.pending_side_button_events.Count > 0 do
        match state.pending_side_button_events.Dequeue() with
        | ButtonDown(button, window) when ViewNavigationState.foreground_root_window () = window ->
            handle_down state button window
        | ButtonDown _ -> ()
        | ButtonUp button -> finish state button

    ViewNavigationState.stop_timer_if_idle state

let poll (state: State) (button: SideButton) =
    match ViewNavigationState.get_button_state state button with
    | Released -> ()
    | HoldActive _
    | TogglePressed _
    | ToggleReleasePressed when not (is_down button) -> finish state button
    | ToggleLatched window when ViewNavigationState.foreground_root_window () <> window ->
        stop_toggle state button Released
    | ToggleLatched _ when is_down button -> stop_toggle state button ToggleReleasePressed
    | HoldActive _
    | TogglePressed _
    | ToggleLatched _
    | ToggleReleasePressed -> ()

let configured_mode (mode: MouseButtonPivotMode) =
    match mode with
    | MouseButtonPivotMode.Off -> Disabled
    | MouseButtonPivotMode.Hold -> Hold
    | MouseButtonPivotMode.Toggle -> Toggle
    | _ -> Disabled
