module RhinosCanFly.Platform.Win.MouseButtonOverrides

open System
open System.Diagnostics
open Rhino.UI
open RhinosCanFly
open RhinosCanFly.Platform.Win.ViewNavigationTypes

let state = create_state ()

type SideButtonCallback() =
    inherit MouseCallback()

    override _.OnMouseDown(event: MouseCallbackEventArgs) =
        try
            let exitsNavigation =
                ViewNavigationState.left_mouse_exit_enabled state
                && (state.routing.exit_on_mouse_left || ViewNavigationState.exit_key_down state)
                && event.MouseButton = Rhino.UI.MouseButton.Left
                && (ViewNavigationState.any_button_engaged state
                    || ViewNavigationState.view_latch_engaged state)

            if exitsNavigation then
                event.Cancel <- true

                match ViewNavigationState.release_all state with
                | Ok() -> ()
                | Error error -> Debug.WriteLine $"RhinosCanFly mouse override exit: {error}"
            else
                if ViewNavigationState.event_has_button Mouse4 event.Button then
                    SideButtonTransitions.handle_down
                        state
                        Mouse4
                        event.ViewportPoint
                        (SideButtonTransitions.event_root_window event)

                if ViewNavigationState.event_has_button Mouse5 event.Button then
                    SideButtonTransitions.handle_down
                        state
                        Mouse5
                        event.ViewportPoint
                        (SideButtonTransitions.event_root_window event)
        with error ->
            Debug.WriteLine $"RhinosCanFly mouse override callback: {error.Message}"

    override _.OnMouseMove(event: MouseCallbackEventArgs) =
        try
            if ViewNavigationState.side_button_routing_enabled state then
                SideButtonTransitions.update_from_move state Mouse4 event
                SideButtonTransitions.update_from_move state Mouse5 event
        with error ->
            Debug.WriteLine $"RhinosCanFly mouse override callback: {error.Message}"

    override _.OnMouseUp(event: MouseCallbackEventArgs) =
        try
            if ViewNavigationState.event_has_button Mouse4 event.Button then
                SideButtonTransitions.finish state Mouse4

            if ViewNavigationState.event_has_button Mouse5 event.Button then
                SideButtonTransitions.finish state Mouse5
        with error ->
            Debug.WriteLine $"RhinosCanFly mouse override callback: {error.Message}"

let callback = SideButtonCallback()

let refresh_callback_enabled () =
    callback.Enabled <-
        state.lifecycle = Available
        && (ViewNavigationState.side_button_routing_enabled state
            || ViewNavigationState.left_mouse_exit_enabled state)

state.poll_timer.Tick.Add(fun (_: EventArgs) ->
    try
        let foreground = ViewNavigationState.foreground_root_window ()

        if
            SideButtonTransitions.lost_focus foreground state.mouse4
            || SideButtonTransitions.lost_focus foreground state.mouse5
        then
            match ViewNavigationState.release_all state with
            | Ok() -> ()
            | Error error -> Debug.WriteLine $"RhinosCanFly mouse override focus loss: {error}"
        elif ViewNavigationState.exit_key_down state then
            match ViewNavigationState.release_all state with
            | Ok() -> ()
            | Error error -> Debug.WriteLine $"RhinosCanFly mouse override exit: {error}"
        else
            SideButtonTransitions.poll state Mouse4
            SideButtonTransitions.poll state Mouse5

            ViewLatchTransitions.update state
            SideButtonTransitions.update_middle_mouse_modifiers state
    with error ->
        Debug.WriteLine $"RhinosCanFly mouse override timer: {error.Message}")

let handle_keyboard_key_down (virtualKey: int) =
    try
        if
            state.lifecycle = Available
            && (ViewNavigationState.any_button_engaged state
                || ViewNavigationState.view_latch_engaged state)
            && ViewNavigationState.exit_binding_down_for_event state virtualKey
        then
            match ViewNavigationState.release_all state with
            | Ok() -> true
            | Error error ->
                Debug.WriteLine $"RhinosCanFly mouse override exit: {error}"
                false
        else
            false
    with error ->
        Debug.WriteLine $"RhinosCanFly mouse override keyboard hook: {error.Message}"
        false

let keyboard_hook =
    match Win32.install_keyboard_hook handle_keyboard_key_down with
    | Ok hook -> Some hook
    | Error error ->
        Debug.WriteLine $"RhinosCanFly mouse override keyboard hook: {error}"
        None

let right_click_enabled () =
    ViewLatchTransitions.right_click_enabled state

let handle_right_click (window: RootWindow) =
    ViewLatchTransitions.handle_right_click state window

let start_view_latch (window: RootWindow) (mode: ViewLatchMode) =
    ViewLatchTransitions.start_or_switch state window mode

let stop_view_latch (mode: ViewLatchMode) = ViewLatchTransitions.stop state mode

let view_latch_is (mode: ViewLatchMode) = ViewLatchTransitions.is_mode state mode

let apply (config: MouseOverrideConfig) =
    if state.lifecycle = ShutDown then
        Error "Mouse button overrides have already shut down."
    else
        match ViewNavigationState.release_all state with
        | Error error -> Error error
        | Ok() ->
            state.routing <-
                { mouse4 = SideButtonTransitions.configured_mode config.mouse4
                  mouse5 = SideButtonTransitions.configured_mode config.mouse5
                  shift_right_click = ViewLatchTransitions.configured_mode config.shift_right_click
                  alt_right_click = ViewLatchTransitions.configured_mode config.alt_right_click
                  exit = config.exit_binding
                  exit_on_mouse_left = config.exit_on_left
                  exit_on_mouse_right = config.exit_on_right }

            refresh_callback_enabled ()
            Ok()

let suspend () =
    if state.lifecycle = ShutDown then
        Error "Mouse button overrides have already shut down."
    else
        callback.Enabled <- false

        match ViewNavigationState.release_all state with
        | Ok() ->
            state.lifecycle <- Suspended
            Ok()
        | Error error ->
            refresh_callback_enabled ()
            Error error

let resume () =
    if state.lifecycle <> ShutDown then
        state.lifecycle <- Available
        refresh_callback_enabled ()

let shutdown () =
    if state.lifecycle <> ShutDown then
        state.lifecycle <- ShutDown
        callback.Enabled <- false

        match keyboard_hook with
        | Some hook ->
            match Win32.remove_keyboard_hook hook with
            | Ok() -> ()
            | Error error -> Debug.WriteLine $"RhinosCanFly mouse override keyboard hook: {error}"
        | None -> ()

        match ViewNavigationState.release_all state with
        | Ok() -> ()
        | Error error -> Debug.WriteLine $"RhinosCanFly mouse override shutdown: {error}"

        state.poll_timer.Dispose()
