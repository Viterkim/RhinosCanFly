module RhinosCanFly.Platform.Win.MouseButtonOverrides

#nowarn "44"

open System
open System.Diagnostics
open System.Drawing
open Rhino
open Rhino.UI
open RhinosCanFly
open RhinosCanFly.Platform.Win.ViewNavigationTypes

let state = create_state ()
let flightKeyboard = FlightKeyboardSuppression.create ()

type NavigationExitCallback() =
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
        with error ->
            Debug.WriteLine $"RhinosCanFly mouse override callback: {error.Message}"

let callback = NavigationExitCallback()

let refresh_callback_enabled () =
    callback.Enabled <- state.lifecycle = Available && ViewNavigationState.left_mouse_exit_enabled state

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

let handle_keyboard_event (virtualKey: int) (keyReleased: bool) =
    try
        if FlightKeyboardSuppression.contains virtualKey flightKeyboard then
            true
        elif
            not keyReleased
            && state.lifecycle = Available
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
    match Win32.install_keyboard_hook handle_keyboard_event with
    | Ok hook -> Some hook
    | Error error ->
        Debug.WriteLine $"RhinosCanFly mouse override keyboard hook: {error}"
        None

let point_over_view (point: Point) =
    RhinoDoc.OpenDocuments()
    |> Array.exists (fun (document: RhinoDoc) ->
        document.Views.GetViewList(true, true)
        |> Array.exists (fun (view: Rhino.Display.RhinoView) -> view.ScreenRectangle.Contains point))

let side_button_from_data (mouseData: uint32) =
    match mouseData >>> 16 with
    | Win32Native.XBUTTON1 -> Some Mouse4
    | Win32Native.XBUTTON2 -> Some Mouse5
    | _ -> None

let handle_mouse_event (message: int) (mouseData: uint32) (point: Point) (window: nativeint) =
    try
        match side_button_from_data mouseData with
        | None -> false
        | Some button when
            state.lifecycle <> Available
            || ViewNavigationState.mode_for state button = Disabled
            ->
            false
        | Some button ->
            let isDown =
                message = Win32Native.WM_XBUTTONDOWN || message = Win32Native.WM_XBUTTONDBLCLK

            let isUp = message = Win32Native.WM_XBUTTONUP

            if isDown && point_over_view point then
                let rootWindow =
                    if window = nativeint 0 then
                        ViewNavigationState.foreground_root_window ()
                    else
                        ViewNavigationState.root_window window

                SideButtonTransitions.handle_down state button rootWindow
                true
            elif isUp && ViewNavigationState.get_button_state state button <> Released then
                SideButtonTransitions.finish state button
                true
            else
                false
    with error ->
        Debug.WriteLine $"RhinosCanFly mouse override hook: {error.Message}"
        false

let mutable mouse_hook: Win32Native.WindowsHook option = None

let install_mouse_hook () =
    match mouse_hook with
    | Some _ -> Ok()
    | None ->
        match Win32.install_mouse_hook handle_mouse_event with
        | Ok hook ->
            mouse_hook <- Some hook
            Ok()
        | Error error -> Error error

let remove_mouse_hook () =
    match mouse_hook with
    | None -> Ok()
    | Some hook ->
        match Win32.remove_hook hook with
        | Ok() ->
            mouse_hook <- None
            Ok()
        | Error error -> Error error

let refresh_mouse_hook () =
    if
        state.lifecycle = Available
        && ViewNavigationState.side_button_routing_enabled state
    then
        install_mouse_hook ()
    else
        remove_mouse_hook ()

let suppress_flight_keyboard (bindings: FlightBindings) =
    match keyboard_hook with
    | Some _ ->
        FlightKeyboardSuppression.start bindings flightKeyboard
        Ok()
    | None -> Error "The keyboard hook is unavailable."

let release_flight_keyboard () =
    FlightKeyboardSuppression.stop flightKeyboard

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
            refresh_mouse_hook ()

let suspend () =
    if state.lifecycle = ShutDown then
        Error "Mouse button overrides have already shut down."
    else
        callback.Enabled <- false

        match ViewNavigationState.release_all state with
        | Error error ->
            refresh_callback_enabled ()
            Error error
        | Ok() ->
            match remove_mouse_hook () with
            | Ok() ->
                state.lifecycle <- Suspended
                Ok()
            | Error error ->
                refresh_callback_enabled ()
                Error error

let resume () =
    if state.lifecycle = ShutDown then
        Error "Mouse button overrides have already shut down."
    else
        state.lifecycle <- Available
        refresh_callback_enabled ()
        refresh_mouse_hook ()

let shutdown () =
    if state.lifecycle <> ShutDown then
        state.lifecycle <- ShutDown
        callback.Enabled <- false
        release_flight_keyboard ()

        match keyboard_hook with
        | Some hook ->
            match Win32.remove_hook hook with
            | Ok() -> ()
            | Error error -> Debug.WriteLine $"RhinosCanFly mouse override keyboard hook: {error}"
        | None -> ()

        match remove_mouse_hook () with
        | Ok() -> ()
        | Error error -> Debug.WriteLine $"RhinosCanFly mouse override hook: {error}"

        match ViewNavigationState.release_all state with
        | Ok() -> ()
        | Error error -> Debug.WriteLine $"RhinosCanFly mouse override shutdown: {error}"

        state.poll_timer.Dispose()
