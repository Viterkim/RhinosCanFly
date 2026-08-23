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

let navigation_mode (action: MouseGestureAction) =
    match action with
    | MouseGestureAction.TogglePivot
    | MouseGestureAction.HoldPivot -> ValueSome Pivot
    | MouseGestureAction.TogglePan
    | MouseGestureAction.HoldPan -> ValueSome Pan
    | MouseGestureAction.Off
    | MouseGestureAction.Retarget
    | _ -> ValueNone

let prepare_navigation (state: State) (host: ViewportHostIdentity) (mode: ViewNavigationMode) =
    match state.routing.prepare_navigation host mode with
    | Ok prepared -> ValueSome prepared
    | Error error ->
        Debug.WriteLine $"RhinosCanFly mouse override target: {error}"
        ValueNone

let finish (state: State) (button: SideButton) =
    match ViewNavigationState.get_button_state state button with
    | Released -> ()
    | TogglePressed host -> ViewNavigationState.set_button_state state button (ToggleLatched host)
    | ToggleLatched _ -> ()
    | ToggleReleasePressed -> ViewNavigationState.set_button_state state button Released

    ViewNavigationState.stop_timer_if_idle state

let stop_toggle (state: State) (button: SideButton) (nextState: SideButtonState) =
    ViewNavigationState.set_button_state state button nextState
    ViewNavigationState.stop_timer_if_idle state

let toggle (state: State) (button: SideButton) (host: ViewportHostIdentity) =
    match ViewNavigationState.get_button_state state button with
    | Released ->
        ViewNavigationState.set_button_state state button (TogglePressed host)
        ViewNavigationState.keep_timer_running state
    | ToggleLatched _ -> stop_toggle state button ToggleReleasePressed
    | TogglePressed _
    | ToggleReleasePressed -> ()

let handle_down (state: State) (button: SideButton) (host: ViewportHostIdentity) =
    let mode = ViewNavigationState.mode_for state button

    let otherButtonEngaged =
        match button with
        | Mouse4 ->
            match state.mouse5 with
            | Released -> false
            | TogglePressed _
            | ToggleLatched _
            | ToggleReleasePressed -> true
        | Mouse5 ->
            match state.mouse4 with
            | Released -> false
            | TogglePressed _
            | ToggleLatched _
            | ToggleReleasePressed -> true

    if otherButtonEngaged then
        ()
    elif ViewNavigationState.view_latch_engaged state then
        match ViewLatchTransitions.release state with
        | Error error -> Debug.WriteLine $"RhinosCanFly mouse override: {error}"
        | Ok() ->
            match navigation_mode mode with
            | ValueSome _ ->
                ViewNavigationState.set_button_state state button ToggleReleasePressed
                ViewNavigationState.keep_timer_running state
            | ValueNone -> ()
    else
        match navigation_mode mode with
        | ValueSome navigationMode ->
            match ViewNavigationState.get_button_state state button with
            | Released ->
                match prepare_navigation state host navigationMode with
                | ValueSome prepared -> toggle state button prepared
                | ValueNone -> ()
            | TogglePressed _
            | ToggleLatched _
            | ToggleReleasePressed -> toggle state button host
        | ValueNone -> ()

let process_hook_events (state: State) =
    while state.lifecycle = Available && state.pending_side_button_events.Count > 0 do
        match state.pending_side_button_events.Dequeue() with
        | ButtonDown(button, host) when ViewNavigationState.foreground_root_window () = host.root_window ->
            handle_down state button host
        | ButtonDown _ -> ()
        | ButtonUp button -> finish state button

    ViewNavigationState.stop_timer_if_idle state

let poll (state: State) (button: SideButton) =
    match ViewNavigationState.get_button_state state button with
    | Released -> ()
    | TogglePressed _
    | ToggleReleasePressed when not (is_down button) -> finish state button
    | ToggleLatched host when ViewNavigationState.foreground_root_window () <> host.root_window ->
        stop_toggle state button Released
    | ToggleLatched _ when is_down button -> stop_toggle state button ToggleReleasePressed
    | TogglePressed _
    | ToggleLatched _
    | ToggleReleasePressed -> ()
