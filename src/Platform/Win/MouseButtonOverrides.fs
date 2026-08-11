module RhinosCanFly.Platform.Win.MouseButtonOverrides

#nowarn "44"

open System
open System.Diagnostics
open Rhino
open Rhino.Display
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
                state.navigation_exit_requested <- true
                ViewNavigationState.keep_timer_running state
        with error ->
            Debug.WriteLine $"RhinosCanFly mouse override callback: {error.Message}"

let callback = NavigationExitCallback()

let refresh_callback_enabled () =
    callback.Enabled <- state.lifecycle = Available && ViewNavigationState.left_mouse_exit_enabled state

let handle_keyboard_event (event: Win32.KeyboardHookEvent) =
    try
        if FlightKeyboardSuppression.handle_event event flightKeyboard then
            if event.released && not (FlightKeyboardSuppression.is_active flightKeyboard) then
                ViewNavigationState.keep_timer_running state

            true
        elif
            not event.released
            && state.lifecycle = Available
            && (ViewNavigationState.any_button_engaged state
                || ViewNavigationState.view_latch_engaged state)
            && ViewNavigationState.exit_binding_down_for_event state event.physical_key
        then
            FlightKeyboardSuppression.suppress_key_down event.physical_key flightKeyboard
            state.navigation_exit_requested <- true
            ViewNavigationState.keep_timer_running state
            true
        else
            false
    with error ->
        Debug.WriteLine $"RhinosCanFly mouse override keyboard hook: {error.Message}"
        false

let mutable keyboard_hook: Win32Native.WindowsHook option = None
let mutable keyboard_unhook_attempts = 0

[<Literal>]
let maximum_unhook_attempts = 3

let keyboard_hook_needed () =
    state.lifecycle <> ShutDown
    && (FlightKeyboardSuppression.requires_hook flightKeyboard
        || (state.lifecycle = Available
            && (state.pending_side_button_events.Count > 0
                || ViewNavigationState.any_button_engaged state
                || ViewNavigationState.view_latch_engaged state)))

let install_keyboard_hook () =
    match keyboard_hook with
    | Some _ -> Ok()
    | None ->
        match Win32.install_keyboard_hook handle_keyboard_event with
        | Ok hook ->
            keyboard_hook <- Some hook
            Ok()
        | Error error -> Error error

let remove_keyboard_hook () =
    match keyboard_hook with
    | None -> Ok()
    | Some hook ->
        match Win32.remove_hook hook with
        | Ok() ->
            keyboard_hook <- None
            Ok()
        | Error error -> Error error

let refresh_keyboard_hook () =
    if keyboard_hook_needed () then
        keyboard_unhook_attempts <- 0
        install_keyboard_hook ()
    else
        match remove_keyboard_hook () with
        | Ok() ->
            keyboard_unhook_attempts <- 0
            Ok()
        | Error error ->
            Debug.WriteLine $"RhinosCanFly mouse override keyboard hook: {error}"

            keyboard_unhook_attempts <- keyboard_unhook_attempts + 1

            if keyboard_unhook_attempts < maximum_unhook_attempts then
                ViewNavigationState.keep_timer_running state

            Ok()

[<Struct>]
type ViewWindow =
    { serial_number: uint32
      handle: nativeint
      root: RootWindow }

let mutable view_windows: ViewWindow array = Array.empty
let mutable view_events_subscribed = false

let refresh_view_windows () =
    try
        view_windows <-
            RhinoDoc.OpenDocuments()
            |> Array.collect (fun (document: RhinoDoc) -> document.Views.GetViewList(true, true))
            |> Array.choose (fun (view: RhinoView) ->
                if isNull view || view.Handle = nativeint 0 then
                    None
                else
                    Some
                        { serial_number = view.RuntimeSerialNumber
                          handle = view.Handle
                          root = ViewNavigationState.root_window view.Handle })
    with error ->
        view_windows <- Array.empty
        Debug.WriteLine $"RhinosCanFly viewport window refresh: {error.Message}"

let view_created =
    EventHandler<ViewEventArgs>(fun (_: obj) (event: ViewEventArgs) ->
        try
            let view = event.View

            if not (isNull view) && view.Handle <> nativeint 0 then
                let created =
                    { serial_number = view.RuntimeSerialNumber
                      handle = view.Handle
                      root = ViewNavigationState.root_window view.Handle }

                let remaining =
                    view_windows
                    |> Array.filter (fun (candidate: ViewWindow) -> candidate.serial_number <> created.serial_number)

                view_windows <- Array.append remaining [| created |]
        with error ->
            Debug.WriteLine $"RhinosCanFly viewport window created: {error.Message}")

let view_destroyed =
    EventHandler<ViewEventArgs>(fun (_: obj) (event: ViewEventArgs) ->
        try
            let view = event.View

            if not (isNull view) then
                let serialNumber = view.RuntimeSerialNumber

                view_windows <-
                    view_windows
                    |> Array.filter (fun (candidate: ViewWindow) -> candidate.serial_number <> serialNumber)
        with error ->
            view_windows <- Array.empty
            Debug.WriteLine $"RhinosCanFly viewport window destroyed: {error.Message}")

let subscribe_view_events () =
    if not view_events_subscribed then
        RhinoView.Create.AddHandler view_created

        try
            RhinoView.Destroy.AddHandler view_destroyed
            view_events_subscribed <- true
            refresh_view_windows ()
        with error ->
            RhinoView.Create.RemoveHandler view_created
            raise error

let unsubscribe_view_events () =
    if view_events_subscribed then
        RhinoView.Create.RemoveHandler view_created
        RhinoView.Destroy.RemoveHandler view_destroyed
        view_events_subscribed <- false
        view_windows <- Array.empty

let try_view_root (window: nativeint) =
    let mutable index = 0
    let mutable result = ValueNone

    while index < view_windows.Length && ValueOption.isNone result do
        let candidate = view_windows[index]

        if candidate.handle = window || Win32Native.IsChild(candidate.handle, window) then
            result <- ValueSome candidate.root

        index <- index + 1

    result

let side_button_from_data (mouseData: uint32) =
    match mouseData >>> 16 with
    | Win32Native.XBUTTON1 -> Some Mouse4
    | Win32Native.XBUTTON2 -> Some Mouse5
    | _ -> None

let handle_mouse_event (message: int) (mouseData: uint32) (window: nativeint) =
    try
        match side_button_from_data mouseData with
        | None -> false
        | Some button ->
            let isDown =
                message = Win32Native.WM_XBUTTONDOWN || message = Win32Native.WM_XBUTTONDBLCLK

            let isUp = message = Win32Native.WM_XBUTTONUP
            let hookOwnsButton = ViewNavigationState.hook_owns_button state button

            if isUp && hookOwnsButton then
                ViewNavigationState.set_hook_owns_button state button false

                if state.lifecycle = Available then
                    state.pending_side_button_events.Enqueue(ButtonUp button)

                ViewNavigationState.keep_timer_running state
                true
            elif isDown && hookOwnsButton then
                true
            elif
                state.lifecycle <> Available
                || ViewNavigationState.mode_for state button = Disabled
            then
                false
            elif isDown then
                match try_view_root window with
                | ValueNone -> false
                | ValueSome rootWindow ->
                    ViewNavigationState.set_hook_owns_button state button true
                    state.pending_side_button_events.Enqueue(ButtonDown(button, rootWindow))
                    ViewNavigationState.keep_timer_running state
                    true
            else
                false
    with error ->
        Debug.WriteLine $"RhinosCanFly mouse override hook: {error.Message}"
        false

let mutable mouse_hook: Win32Native.WindowsHook option = None
let mutable mouse_unhook_attempts = 0

let install_mouse_hook () =
    match mouse_hook with
    | Some _ -> Ok()
    | None ->
        try
            subscribe_view_events ()

            match Win32.install_mouse_hook handle_mouse_event with
            | Ok hook ->
                mouse_hook <- Some hook
                Ok()
            | Error error ->
                unsubscribe_view_events ()
                Error error
        with error ->
            Error $"Could not track Rhino viewport windows: {error.Message}"

let remove_mouse_hook () =
    match mouse_hook with
    | None ->
        try
            unsubscribe_view_events ()
            Ok()
        with error ->
            Error $"Could not stop tracking Rhino viewport windows: {error.Message}"
    | Some hook ->
        match Win32.remove_hook hook with
        | Ok() ->
            mouse_hook <- None

            try
                unsubscribe_view_events ()
                Ok()
            with error ->
                Error $"Could not stop tracking Rhino viewport windows: {error.Message}"
        | Error error -> Error error

let refresh_mouse_hook () =
    if
        (state.lifecycle = Available
         && ViewNavigationState.side_button_routing_enabled state)
        || ViewNavigationState.hook_owns_any_button state
    then
        mouse_unhook_attempts <- 0
        install_mouse_hook ()
    else
        match remove_mouse_hook () with
        | Ok() ->
            mouse_unhook_attempts <- 0
            Ok()
        | Error error ->
            Debug.WriteLine $"RhinosCanFly mouse override hook: {error}"
            mouse_unhook_attempts <- mouse_unhook_attempts + 1

            if mouse_unhook_attempts < maximum_unhook_attempts then
                ViewNavigationState.keep_timer_running state

            Ok()

let prune_released_side_buttons () =
    if ViewNavigationState.hook_owns_button state Mouse4 then
        if SideButtonTransitions.is_down Mouse4 then
            ViewNavigationState.set_hook_owns_button state Mouse4 true
        else
            ViewNavigationState.observe_hook_button_released state Mouse4

    if ViewNavigationState.hook_owns_button state Mouse5 then
        if SideButtonTransitions.is_down Mouse5 then
            ViewNavigationState.set_hook_owns_button state Mouse5 true
        else
            ViewNavigationState.observe_hook_button_released state Mouse5

let release_after_timer_error (error: exn) =
    Debug.WriteLine $"RhinosCanFly mouse override timer: {error.Message}"

    match ViewNavigationState.release_all state with
    | Ok() -> ()
    | Error cleanupError -> Debug.WriteLine $"RhinosCanFly mouse override timer cleanup: {cleanupError}"

state.poll_timer.Elapsed.Add(fun (_: EventArgs) ->
    try
        if FlightKeyboardSuppression.waiting_for_releases flightKeyboard then
            FlightKeyboardSuppression.prune_released_keys flightKeyboard

        match refresh_keyboard_hook () with
        | Ok() -> ()
        | Error error -> failwith error

        SideButtonTransitions.process_hook_events state
        prune_released_side_buttons ()

        let foreground = ViewNavigationState.foreground_root_window ()

        if
            SideButtonTransitions.lost_focus foreground state.mouse4
            || SideButtonTransitions.lost_focus foreground state.mouse5
        then
            match ViewNavigationState.release_all state with
            | Ok() -> ()
            | Error error -> Debug.WriteLine $"RhinosCanFly mouse override focus loss: {error}"
        elif state.navigation_exit_requested || ViewNavigationState.exit_key_down state then
            match ViewNavigationState.release_all state with
            | Ok() -> ()
            | Error error -> Debug.WriteLine $"RhinosCanFly mouse override exit: {error}"
        else
            SideButtonTransitions.poll state Mouse4
            SideButtonTransitions.poll state Mouse5

            ViewLatchTransitions.update state
            SideButtonTransitions.update_middle_mouse_modifiers state
    with error ->
        release_after_timer_error error

    match refresh_keyboard_hook () with
    | Ok() -> ()
    | Error error -> Debug.WriteLine $"RhinosCanFly mouse override keyboard hook: {error}"

    match refresh_mouse_hook () with
    | Ok() -> ()
    | Error error -> Debug.WriteLine $"RhinosCanFly mouse override hook: {error}"

    let hookRemovalRetryPending =
        (keyboard_unhook_attempts > 0
         && keyboard_unhook_attempts < maximum_unhook_attempts)
        || (mouse_unhook_attempts > 0 && mouse_unhook_attempts < maximum_unhook_attempts)

    if
        FlightKeyboardSuppression.waiting_for_releases flightKeyboard
        || ViewNavigationState.hook_owns_any_button state
        || hookRemovalRetryPending
        || (state.lifecycle = Available
            && (ViewNavigationState.any_button_engaged state
                || ViewNavigationState.view_latch_engaged state))
    then
        ViewNavigationState.keep_timer_running state
    else
        state.poll_timer.Stop())

let suppress_flight_keyboard (bindings: FlightBindings) =
    FlightKeyboardSuppression.start bindings flightKeyboard

    match refresh_keyboard_hook () with
    | Ok() -> Ok()
    | Error error ->
        FlightKeyboardSuppression.stop flightKeyboard
        Error error

let release_flight_keyboard () =
    FlightKeyboardSuppression.stop flightKeyboard

    if FlightKeyboardSuppression.waiting_for_releases flightKeyboard then
        ViewNavigationState.keep_timer_running state

    match refresh_keyboard_hook () with
    | Ok() -> ()
    | Error error -> Debug.WriteLine $"RhinosCanFly mouse override keyboard hook: {error}"

let right_click_enabled () =
    ViewLatchTransitions.right_click_enabled state

let handle_right_click (window: RootWindow) =
    match ViewLatchTransitions.handle_right_click state window with
    | Ok false -> Ok false
    | Error error -> Error error
    | Ok true ->
        match refresh_keyboard_hook () with
        | Ok() -> Ok true
        | Error error ->
            match ViewNavigationState.release_all state with
            | Ok() -> Error error
            | Error cleanupError -> Error $"{error}; cleanup failed: {cleanupError}"

let start_view_latch (window: RootWindow) (mode: ViewLatchMode) (completion: Action option) =
    match install_keyboard_hook () with
    | Error error -> Error error
    | Ok() ->
        match ViewLatchTransitions.start_or_switch state window mode completion with
        | Ok() -> Ok()
        | Error error ->
            match refresh_keyboard_hook () with
            | Ok() -> Error error
            | Error cleanupError -> Error $"{error}; keyboard hook cleanup failed: {cleanupError}"

let stop_view_latch (mode: ViewLatchMode) =
    match ViewLatchTransitions.stop state mode with
    | Error error -> Error error
    | Ok() ->
        match refresh_keyboard_hook () with
        | Ok() -> Ok()
        | Error error -> Error error

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

            match refresh_mouse_hook () with
            | Error error -> Error error
            | Ok() ->
                if ViewNavigationState.hook_owns_any_button state then
                    ViewNavigationState.keep_timer_running state

                refresh_keyboard_hook ()

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
            state.lifecycle <- Suspended

            match refresh_mouse_hook () with
            | Ok() ->
                if ViewNavigationState.hook_owns_any_button state then
                    ViewNavigationState.keep_timer_running state

                match refresh_keyboard_hook () with
                | Ok() -> Ok()
                | Error error -> Error error
            | Error error ->
                state.lifecycle <- Available
                refresh_callback_enabled ()
                Error error

let resume () =
    if state.lifecycle = ShutDown then
        Error "Mouse button overrides have already shut down."
    else
        state.lifecycle <- Available
        refresh_callback_enabled ()

        match refresh_mouse_hook () with
        | Error error -> Error error
        | Ok() -> refresh_keyboard_hook ()

let shutdown () =
    if state.lifecycle <> ShutDown then
        state.lifecycle <- ShutDown
        callback.Enabled <- false
        FlightKeyboardSuppression.reset flightKeyboard
        ViewNavigationState.set_hook_owns_button state Mouse4 false
        ViewNavigationState.set_hook_owns_button state Mouse5 false

        match remove_keyboard_hook () with
        | Ok() -> ()
        | Error error -> Debug.WriteLine $"RhinosCanFly mouse override keyboard hook: {error}"

        match remove_mouse_hook () with
        | Ok() -> ()
        | Error error -> Debug.WriteLine $"RhinosCanFly mouse override hook: {error}"

        match ViewNavigationState.release_all state with
        | Ok() -> ()
        | Error error -> Debug.WriteLine $"RhinosCanFly mouse override shutdown: {error}"

        state.poll_timer.Dispose()
