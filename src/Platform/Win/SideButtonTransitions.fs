module RhinosCanFly.Platform.Win.SideButtonTransitions

open System.Diagnostics
open System.Windows.Forms
open RhinosCanFly
open RhinosCanFly.Platform.Win.ViewNavigationTypes

let is_down (button: SideButton) =
    let key =
        match button with
        | Mouse4 -> Keys.XButton1
        | Mouse5 -> Keys.XButton2

    Win32Native.GetAsyncKeyState(int key) < 0s

let begin_hold (state: State) (button: SideButton) (window: RootWindow) =
    if ViewNavigationState.middle_mouse_down state then
        ViewNavigationState.set_button_state state button (HoldActive window)
        ViewNavigationState.keep_timer_running state
    else
        match Win32.send_middle_mouse true with
        | Ok() ->
            ViewNavigationState.set_button_state state button (HoldActive window)
            state.side_button_restart_pending <- false
            state.middle_mouse_modifiers_down <- ViewNavigationState.view_modifier_down ()
            ViewNavigationState.keep_timer_running state
        | Error error -> Debug.WriteLine $"RhinosCanFly mouse override: {error}"

let finish (state: State) (button: SideButton) =
    match ViewNavigationState.get_button_state state button with
    | Released -> ()
    | TogglePressed window -> ViewNavigationState.set_button_state state button (ToggleLatched window)
    | ToggleLatched _ -> ()
    | ToggleReleasePressed -> ViewNavigationState.set_button_state state button Released
    | HoldActive window ->
        ViewNavigationState.set_button_state state button Released

        if not (ViewNavigationState.middle_mouse_down state) then
            let releaseResult =
                if state.side_button_restart_pending then
                    state.side_button_restart_pending <- false
                    Ok()
                else
                    Win32.send_middle_mouse false

            match releaseResult with
            | Ok() ->
                state.middle_mouse_modifiers_down <- false

                match ViewNavigationState.release_synthetic_shift state with
                | Ok() -> ()
                | Error error -> Debug.WriteLine $"RhinosCanFly mouse override: {error}"
            | Error error ->
                ViewNavigationState.set_button_state state button (HoldActive window)
                Debug.WriteLine $"RhinosCanFly mouse override: {error}"

    ViewNavigationState.stop_timer_if_idle state

let update_middle_mouse_modifiers (state: State) =
    if
        ViewNavigationState.any_button_holds_middle state
        && not (ViewNavigationState.view_latch_engaged state)
    then
        let modifiersDown = ViewNavigationState.view_modifier_down ()

        if state.side_button_restart_pending then
            match Win32.send_middle_mouse true with
            | Ok() ->
                state.side_button_restart_pending <- false
                state.middle_mouse_modifiers_down <- modifiersDown
            | Error error ->
                Debug.WriteLine $"RhinosCanFly mouse override: {error}"

                match ViewNavigationState.release_all state with
                | Ok() -> ()
                | Error cleanupError -> Debug.WriteLine $"RhinosCanFly mouse override cleanup: {cleanupError}"
        elif modifiersDown <> state.middle_mouse_modifiers_down then
            match Win32.send_middle_mouse false with
            | Ok() -> state.side_button_restart_pending <- true
            | Error error -> Debug.WriteLine $"RhinosCanFly mouse override: {error}"

let stop_toggle (state: State) (button: SideButton) (nextState: SideButtonState) =
    let previous = ViewNavigationState.get_button_state state button
    ViewNavigationState.set_button_state state button nextState

    if ViewNavigationState.middle_mouse_down state then
        ViewNavigationState.stop_timer_if_idle state
    else
        let releaseResult =
            if state.side_button_restart_pending then
                state.side_button_restart_pending <- false
                Ok()
            else
                Win32.send_middle_mouse false

        match releaseResult with
        | Ok() ->
            state.middle_mouse_modifiers_down <- false
            ViewNavigationState.stop_timer_if_idle state

            match ViewNavigationState.release_synthetic_shift state with
            | Ok() -> ()
            | Error error -> Debug.WriteLine $"RhinosCanFly mouse override: {error}"
        | Error error ->
            ViewNavigationState.set_button_state state button previous
            Debug.WriteLine $"RhinosCanFly mouse override: {error}"

let toggle (state: State) (button: SideButton) (window: RootWindow) =
    match ViewNavigationState.get_button_state state button with
    | Released ->
        let start () =
            ViewNavigationState.set_button_state state button (TogglePressed window)

            ViewNavigationState.keep_timer_running state

        if ViewNavigationState.middle_mouse_down state then
            start ()
        else
            match Win32.send_middle_mouse true with
            | Ok() ->
                state.side_button_restart_pending <- false
                state.middle_mouse_modifiers_down <- ViewNavigationState.view_modifier_down ()
                start ()
            | Error error -> Debug.WriteLine $"RhinosCanFly mouse override: {error}"
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

let lost_focus (foreground: RootWindow) (buttonState: SideButtonState) =
    match buttonState with
    | HoldActive window
    | TogglePressed window
    | ToggleLatched window -> foreground <> window
    | Released
    | ToggleReleasePressed -> false

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
