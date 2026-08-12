module RhinosCanFly.Platform.Win.MouseButtonOverrides

#nowarn "44"

open System
open System.Diagnostics
open Rhino
open Rhino.Commands
open Rhino.Display
open Rhino.UI
open RhinosCanFly
open RhinosCanFly.Platform.Win.ViewNavigationTypes

let state = create_state ()
let flightKeyboard = FlightKeyboardSuppression.create ()

type HookState =
    | HookAbsent
    | HookInstalled of Win32Native.WindowsHook
    | HookRemovalPending of hook: Win32Native.WindowsHook * error: string * attempts: int
    | HookRemovalAbandoned of hook: Win32Native.WindowsHook * error: string

[<Literal>]
let maximum_hook_removal_attempts = 8

let mutable keyboard_hook_state = HookAbsent
let mutable mouse_hook_state = HookAbsent

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
        let injected = Win32.injected_input event.extra_info

        if
            not injected
            && (event.physical_key = Win32Native.VK_LSHIFT
                || event.physical_key = Win32Native.VK_RSHIFT)
        then
            ViewNavigationState.observe_physical_shift state event.physical_key event.released

        let hookActive =
            match keyboard_hook_state with
            | HookInstalled _ -> true
            | HookAbsent
            | HookRemovalPending _
            | HookRemovalAbandoned _ -> false

        let navigationEngaged =
            hookActive
            && state.lifecycle = Available
            && (ViewNavigationState.any_button_engaged state
                || ViewNavigationState.view_latch_engaged state)

        if navigationEngaged then
            ViewNavigationState.keep_timer_running state

        if injected then
            false
        elif hookActive && FlightKeyboardSuppression.handle_event event flightKeyboard then
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

let keyboard_hook_needed () =
    state.lifecycle <> ShutDown
    && (FlightKeyboardSuppression.requires_hook flightKeyboard
        || ViewNavigationState.synthetic_shift_owned state
        || (state.lifecycle = Available
            && (state.pending_side_button_events.Count > 0
                || ViewNavigationState.any_button_engaged state
                || ViewNavigationState.view_latch_engaged state)))

let install_keyboard_hook () =
    match keyboard_hook_state with
    | HookInstalled _ -> Ok()
    | HookRemovalPending(_, error, _)
    | HookRemovalAbandoned(_, error) -> Error $"The previous keyboard hook could not be removed: {error}"
    | HookAbsent ->
        match Win32.install_keyboard_hook handle_keyboard_event with
        | Ok hook ->
            keyboard_hook_state <- HookInstalled hook
            InputDiagnostics.record InputDiagnostics.EventKind.HookInstalled 1L 0L
            Ok()
        | Error error -> Error error

let remove_keyboard_hook () =
    match keyboard_hook_state with
    | HookAbsent -> Ok()
    | HookInstalled hook ->
        match Win32.remove_hook hook with
        | Ok() ->
            keyboard_hook_state <- HookAbsent
            InputDiagnostics.record InputDiagnostics.EventKind.HookRemoved 1L 0L
            Ok()
        | Error error ->
            keyboard_hook_state <- HookRemovalPending(hook, error, 1)
            InputDiagnostics.record InputDiagnostics.EventKind.HookRemovalFailed 1L 1L
            ViewNavigationState.keep_watchdog_running state
            Error error
    | HookRemovalPending(hook, _, _)
    | HookRemovalAbandoned(hook, _) ->
        match Win32.remove_hook hook with
        | Ok() ->
            keyboard_hook_state <- HookAbsent
            InputDiagnostics.record InputDiagnostics.EventKind.HookRemoved 1L 0L
            Ok()
        | Error error ->
            let nextAttempt =
                match keyboard_hook_state with
                | HookRemovalPending(_, _, previousAttempts) -> previousAttempts + 1
                | HookRemovalAbandoned _ -> maximum_hook_removal_attempts
                | HookAbsent
                | HookInstalled _ -> 1

            if nextAttempt >= maximum_hook_removal_attempts then
                keyboard_hook_state <- HookRemovalAbandoned(hook, error)
            else
                keyboard_hook_state <- HookRemovalPending(hook, error, nextAttempt)
                ViewNavigationState.keep_watchdog_running state

            InputDiagnostics.record InputDiagnostics.EventKind.HookRemovalFailed 1L (int64 nextAttempt)
            Error error

let refresh_keyboard_hook () =
    if keyboard_hook_needed () then
        install_keyboard_hook ()
    else
        remove_keyboard_hook ()

let refresh_keyboard_after (result: Result<'Value, string>) =
    match result, refresh_keyboard_hook () with
    | Ok value, Ok() -> Ok value
    | Error error, Ok() -> Error error
    | Ok _, Error hookError -> Error hookError
    | Error error, Error hookError -> Error $"{error}; keyboard hook cleanup failed: {hookError}"

[<Struct>]
type ViewWindow =
    { serial_number: uint32
      handle: nativeint }

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
                          handle = view.Handle })
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
                      handle = view.Handle }

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

        if
            Win32Native.IsWindow candidate.handle
            && Win32Native.IsWindowEnabled candidate.handle
            && (candidate.handle = window || Win32Native.IsChild(candidate.handle, window))
        then
            result <- ValueSome(ViewNavigationState.root_window candidate.handle)

        index <- index + 1

    result

let side_button_from_data (mouseData: uint32) =
    match mouseData >>> 16 with
    | Win32Native.XBUTTON1 -> Some Mouse4
    | Win32Native.XBUTTON2 -> Some Mouse5
    | _ -> None

let handle_mouse_event (event: Win32.MouseHookEvent) =
    try
        let injected = Win32.injected_input event.extra_info

        if
            not injected
            && event.message = Win32Native.WM_MBUTTONDOWN
            && state.synthetic_middle <> MiddleReleased
        then
            ViewNavigationState.observe_physical_middle state true
            false
        elif
            not injected
            && event.message = Win32Native.WM_MBUTTONUP
            && state.synthetic_middle <> MiddleReleased
        then
            ViewNavigationState.observe_physical_middle state false

            if state.synthetic_middle = MiddleReleasePending then
                ViewNavigationState.keep_watchdog_running state

            false
        else
            match side_button_from_data event.mouse_data with
            | None -> false
            | Some button ->
                let hookActive =
                    match mouse_hook_state with
                    | HookInstalled _ -> true
                    | HookAbsent
                    | HookRemovalPending _
                    | HookRemovalAbandoned _ -> false

                let isDown =
                    event.message = Win32Native.WM_XBUTTONDOWN
                    || event.message = Win32Native.WM_XBUTTONDBLCLK

                let isUp = event.message = Win32Native.WM_XBUTTONUP
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
                    not hookActive
                    || state.lifecycle <> Available
                    || ViewNavigationState.mode_for state button = Disabled
                then
                    false
                elif isDown then
                    match try_view_root event.hook_window, try_view_root event.point_window with
                    | ValueSome hookRoot, ValueSome pointRoot when
                        hookRoot = pointRoot && hookRoot = ViewNavigationState.foreground_root_window ()
                        ->
                        ViewNavigationState.set_hook_owns_button state button true
                        state.pending_side_button_events.Enqueue(ButtonDown(button, hookRoot))
                        ViewNavigationState.keep_timer_running state
                        true
                    | _ -> false
                else
                    false
    with error ->
        Debug.WriteLine $"RhinosCanFly mouse override hook: {error.Message}"
        false

let install_mouse_hook () =
    match mouse_hook_state with
    | HookInstalled _ -> Ok()
    | HookRemovalPending(_, error, _)
    | HookRemovalAbandoned(_, error) -> Error $"The previous mouse hook could not be removed: {error}"
    | HookAbsent ->
        try
            subscribe_view_events ()

            match Win32.install_mouse_hook handle_mouse_event with
            | Ok hook ->
                mouse_hook_state <- HookInstalled hook
                InputDiagnostics.record InputDiagnostics.EventKind.HookInstalled 2L 0L
                Ok()
            | Error error ->
                unsubscribe_view_events ()
                Error error
        with error ->
            Error $"Could not track Rhino viewport windows: {error.Message}"

let remove_mouse_hook () =
    match mouse_hook_state with
    | HookAbsent ->
        try
            unsubscribe_view_events ()
            Ok()
        with error ->
            Error $"Could not stop tracking Rhino viewport windows: {error.Message}"
    | HookInstalled hook ->
        match Win32.remove_hook hook with
        | Ok() ->
            mouse_hook_state <- HookAbsent
            InputDiagnostics.record InputDiagnostics.EventKind.HookRemoved 2L 0L

            try
                unsubscribe_view_events ()
                Ok()
            with error ->
                Error $"Could not stop tracking Rhino viewport windows: {error.Message}"
        | Error error ->
            mouse_hook_state <- HookRemovalPending(hook, error, 1)
            InputDiagnostics.record InputDiagnostics.EventKind.HookRemovalFailed 2L 1L
            ViewNavigationState.keep_watchdog_running state
            Error error
    | HookRemovalPending(hook, _, _)
    | HookRemovalAbandoned(hook, _) ->
        match Win32.remove_hook hook with
        | Ok() ->
            mouse_hook_state <- HookAbsent
            InputDiagnostics.record InputDiagnostics.EventKind.HookRemoved 2L 0L

            try
                unsubscribe_view_events ()
                Ok()
            with error ->
                Error $"Could not stop tracking Rhino viewport windows: {error.Message}"
        | Error error ->
            let nextAttempt =
                match mouse_hook_state with
                | HookRemovalPending(_, _, previousAttempts) -> previousAttempts + 1
                | HookRemovalAbandoned _ -> maximum_hook_removal_attempts
                | HookAbsent
                | HookInstalled _ -> 1

            if nextAttempt >= maximum_hook_removal_attempts then
                mouse_hook_state <- HookRemovalAbandoned(hook, error)
            else
                mouse_hook_state <- HookRemovalPending(hook, error, nextAttempt)
                ViewNavigationState.keep_watchdog_running state

            InputDiagnostics.record InputDiagnostics.EventKind.HookRemovalFailed 2L (int64 nextAttempt)
            Error error

let refresh_mouse_hook () =
    if
        (state.lifecycle = Available
         && ViewNavigationState.side_button_routing_enabled state)
        || ViewNavigationState.hook_owns_any_button state
        || ViewNavigationState.any_button_engaged state
        || ViewNavigationState.view_latch_engaged state
        || ViewNavigationState.synthetic_input_owned state
    then
        install_mouse_hook ()
    else
        remove_mouse_hook ()

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

let hook_removal_pending () =
    match keyboard_hook_state with
    | HookRemovalPending _ -> true
    | HookAbsent
    | HookInstalled _
    | HookRemovalAbandoned _ ->
        match mouse_hook_state with
        | HookRemovalPending _ -> true
        | HookAbsent
        | HookInstalled _
        | HookRemovalAbandoned _ -> false

let poll_requirement () =
    if ViewNavigationState.fast_poll_required state then
        PollFast
    elif
        FlightKeyboardSuppression.waiting_for_releases flightKeyboard
        || ViewNavigationState.hook_owns_any_button state
        || ViewNavigationState.synthetic_input_owned state
        || Option.isSome state.pending_view_completion
        || hook_removal_pending ()
        || (state.lifecycle = Available
            && (ViewNavigationState.any_button_engaged state
                || ViewNavigationState.view_latch_engaged state))
    then
        PollWatchdog
    else
        PollStopped

let apply_poll_requirement () =
    match poll_requirement () with
    | PollFast -> ViewNavigationState.keep_timer_running state
    | PollWatchdog -> ViewNavigationState.keep_watchdog_running state
    | PollStopped ->
        if state.poll_timer.Started then
            state.poll_timer.Stop()
            InputDiagnostics.record InputDiagnostics.EventKind.TimerStopped 0L 0L

let poll_timer_elapsed () =
    try
        try
            if FlightKeyboardSuppression.waiting_for_releases flightKeyboard then
                FlightKeyboardSuppression.prune_released_keys flightKeyboard

            ViewNavigationState.reconcile_release_pending_physical_input state

            if
                not (ViewNavigationState.any_button_engaged state)
                && not (ViewNavigationState.view_latch_engaged state)
                && ViewNavigationState.synthetic_input_owned state
            then
                match ViewNavigationState.release_synthetic_input state with
                | Ok() -> ()
                | Error error -> Debug.WriteLine $"RhinosCanFly synthetic input cleanup: {error}"

            if
                not (ViewNavigationState.synthetic_input_owned state)
                && Option.isSome state.pending_view_completion
            then
                match ViewNavigationState.complete_pending_view_latch state with
                | Ok() -> ()
                | Error error -> Debug.WriteLine $"RhinosCanFly latched view manipulation: {error}"

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

        apply_poll_requirement ()
    with error ->
        release_after_timer_error error

        try
            apply_poll_requirement ()
        with stopError ->
            Debug.WriteLine $"RhinosCanFly mouse override timer scheduling: {stopError}"

state.poll_timer.Elapsed.Add(fun (_: EventArgs) ->
    let startedAt = Stopwatch.GetTimestamp()

    try
        poll_timer_elapsed ()
    finally
        let elapsed = Stopwatch.GetTimestamp() - startedAt
        state.poll_callback_count <- state.poll_callback_count + 1L
        state.last_poll_duration_ticks <- elapsed
        state.maximum_poll_duration_ticks <- max state.maximum_poll_duration_ticks elapsed)

let keeps_navigation_active (commandName: string) =
    String.Equals(commandName, "RhinosCanFlyPivot", StringComparison.Ordinal)
    || String.Equals(commandName, "RhinosCanFlyPan", StringComparison.Ordinal)

let command_began =
    EventHandler<CommandEventArgs>(fun (_: obj) (event: CommandEventArgs) ->
        try
            if
                not (keeps_navigation_active event.CommandEnglishName)
                && state.lifecycle = Available
                && (state.pending_side_button_events.Count > 0
                    || ViewNavigationState.hook_owns_any_button state
                    || ViewNavigationState.synthetic_input_owned state
                    || ViewNavigationState.any_button_engaged state
                    || ViewNavigationState.view_latch_engaged state)
            then
                let ownedSyntheticInput = ViewNavigationState.synthetic_input_owned state

                match ViewNavigationState.release_all state with
                | Ok() -> ()
                | Error error -> Debug.WriteLine $"RhinosCanFly command navigation cleanup: {error}"

                if ownedSyntheticInput then
                    RhinoApp.ReleaseMouseCapture() |> ignore
                    InputDiagnostics.record InputDiagnostics.EventKind.ViewCaptureReleased 0L 0L

                match refresh_keyboard_hook () with
                | Ok() -> ()
                | Error error -> Debug.WriteLine $"RhinosCanFly command keyboard hook cleanup: {error}"

                match refresh_mouse_hook () with
                | Ok() -> ()
                | Error error -> Debug.WriteLine $"RhinosCanFly command mouse hook cleanup: {error}"

                apply_poll_requirement ()
        with error ->
            Debug.WriteLine $"RhinosCanFly command navigation callback: {error}")

do Command.BeginCommand.AddHandler command_began

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

    refresh_keyboard_hook ()

let right_click_enabled () =
    ViewLatchTransitions.right_click_enabled state

let handle_right_click (window: RootWindow) =
    let transition = ViewLatchTransitions.handle_right_click state window

    let hookResult =
        match refresh_keyboard_hook () with
        | Error error -> Error error
        | Ok() -> refresh_mouse_hook ()

    match transition, hookResult with
    | Ok handled, Ok() -> Ok handled
    | Error error, Ok() -> Error error
    | Ok false, Error hookError -> Error hookError
    | Error error, Error hookError -> Error $"{error}; keyboard hook cleanup failed: {hookError}"
    | Ok true, Error hookError ->
        let errors = ResizeArray<string>()
        errors.Add hookError

        match ViewNavigationState.release_all state with
        | Ok() -> ()
        | Error error -> errors.Add $"cleanup failed: {error}"

        match refresh_keyboard_hook () with
        | Ok() -> ()
        | Error error -> errors.Add $"keyboard hook cleanup failed: {error}"

        match refresh_mouse_hook () with
        | Ok() -> ()
        | Error error -> errors.Add $"mouse hook cleanup failed: {error}"

        Error(String.concat "; " errors)

let start_view_latch (window: RootWindow) (mode: ViewLatchMode) (completion: Action option) =
    match install_keyboard_hook () with
    | Error error -> Error error
    | Ok() ->
        match install_mouse_hook () with
        | Ok() ->
            ViewLatchTransitions.start_or_switch state window mode completion
            |> refresh_keyboard_after
        | Error error ->
            match refresh_keyboard_hook () with
            | Ok() -> Error error
            | Error cleanupError -> Error $"{error}; keyboard hook cleanup failed: {cleanupError}"

let stop_view_latch (mode: ViewLatchMode) =
    ViewLatchTransitions.stop state mode |> refresh_keyboard_after

let view_latch_is (mode: ViewLatchMode) = ViewLatchTransitions.is_mode state mode

let apply (config: MouseOverrideConfig) =
    if state.lifecycle = ShutDown then
        Error "Mouse button overrides have already shut down."
    else
        match ViewNavigationState.release_all state |> refresh_keyboard_after with
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

let suspend (reason: InputSuspensionReason) =
    if state.lifecycle = ShutDown then
        Error "Mouse button overrides have already shut down."
    elif state.suspensions.Count > 0 then
        state.next_suspension_id <- state.next_suspension_id + 1L

        let lease =
            { id = state.next_suspension_id
              reason = reason
              released_viewport_input = false
              cleanup_error = state.suspension_cleanup_error }

        state.suspensions.Add(lease.id, lease.reason)
        Ok lease
    else
        callback.Enabled <- false
        let ownedSyntheticInput = ViewNavigationState.synthetic_input_owned state
        let errors = ResizeArray<string>()
        state.lifecycle <- Suspended

        match ViewNavigationState.release_all state |> refresh_keyboard_after with
        | Ok() -> ()
        | Error error -> errors.Add error

        if ownedSyntheticInput then
            RhinoApp.ReleaseMouseCapture() |> ignore
            InputDiagnostics.record InputDiagnostics.EventKind.ViewCaptureReleased 0L 0L

        match refresh_mouse_hook () with
        | Ok() -> ()
        | Error error -> errors.Add $"mouse hook: {error}"

        match refresh_keyboard_hook () with
        | Ok() -> ()
        | Error error -> errors.Add $"keyboard hook: {error}"

        state.next_suspension_id <- state.next_suspension_id + 1L

        let cleanupError =
            if errors.Count = 0 then
                None
            else
                Some(String.concat "; " errors)

        state.suspension_cleanup_error <- cleanupError

        let lease =
            { id = state.next_suspension_id
              reason = reason
              released_viewport_input = ownedSyntheticInput
              cleanup_error = cleanupError }

        state.suspensions.Add(lease.id, lease.reason)
        Ok lease

let resume (lease: InputSuspensionLease) =
    if state.lifecycle = ShutDown then
        Error "Mouse button overrides have already shut down."
    elif not (state.suspensions.Remove lease.id) then
        Ok()
    elif state.suspensions.Count > 0 then
        Ok()
    else
        state.suspension_cleanup_error <- None
        state.lifecycle <- Available
        refresh_callback_enabled ()

        match refresh_mouse_hook () with
        | Error error -> Error error
        | Ok() -> refresh_keyboard_hook ()

let hook_state_name (hookState: HookState) =
    match hookState with
    | HookAbsent -> "absent"
    | HookInstalled _ -> "installed"
    | HookRemovalPending _ -> "removal pending"
    | HookRemovalAbandoned(_, error) -> $"removal abandoned ({error})"

let retry_hook_cleanup () =
    let errors = ResizeArray<string>()
    let ownedSyntheticInput = ViewNavigationState.synthetic_input_owned state

    match ViewNavigationState.release_all state with
    | Ok() -> ()
    | Error error -> errors.Add $"synthetic input: {error}"

    if ownedSyntheticInput then
        RhinoApp.ReleaseMouseCapture() |> ignore
        InputDiagnostics.record InputDiagnostics.EventKind.ViewCaptureReleased 0L 0L

    match keyboard_hook_state with
    | HookRemovalPending _
    | HookRemovalAbandoned _ ->
        match remove_keyboard_hook () with
        | Ok() -> ()
        | Error error -> errors.Add $"keyboard hook: {error}"
    | HookAbsent
    | HookInstalled _ -> ()

    match mouse_hook_state with
    | HookRemovalPending _
    | HookRemovalAbandoned _ ->
        match remove_mouse_hook () with
        | Ok() -> ()
        | Error error -> errors.Add $"mouse hook: {error}"
    | HookAbsent
    | HookInstalled _ -> ()

    match refresh_keyboard_hook () with
    | Ok() -> ()
    | Error error -> errors.Add $"keyboard hook: {error}"

    match refresh_mouse_hook () with
    | Ok() -> ()
    | Error error -> errors.Add $"mouse hook: {error}"

    apply_poll_requirement ()
    List.ofSeq errors

let diagnostic_lines () =
    let ticksToMilliseconds (ticks: int64) =
        float ticks * 1000. / float Stopwatch.Frequency

    let suspensionReasons =
        if state.suspensions.Count = 0 then
            "none"
        else
            state.suspensions.Values |> Seq.map string |> String.concat ", "

    let suspensionCleanupError =
        state.suspension_cleanup_error |> Option.defaultValue "none"

    [| $"Lifecycle: {state.lifecycle}"
       $"Suspensions: {state.suspensions.Count} ({suspensionReasons})"
       $"Suspension cleanup error: {suspensionCleanupError}"
       $"Timer: started={state.poll_timer.Started}; interval={state.poll_timer.Interval:F3}s; requirement={poll_requirement ()}"
       $"Timer callbacks: count={state.poll_callback_count}; last={ticksToMilliseconds state.last_poll_duration_ticks:F3} ms; max={ticksToMilliseconds state.maximum_poll_duration_ticks:F3} ms"
       $"Keyboard hook: {hook_state_name keyboard_hook_state}; active={FlightKeyboardSuppression.is_active flightKeyboard}; pending releases={flightKeyboard.suppressed_keys_down.Count}"
       $"Mouse hook: {hook_state_name mouse_hook_state}; owned buttons={ViewNavigationState.hook_owns_any_button state}; queued events={state.pending_side_button_events.Count}"
       $"Navigation: buttons={ViewNavigationState.any_button_engaged state}; latch={state.view_latch}; pending completion={Option.isSome state.pending_view_completion}; synthetic shift={state.synthetic_shift}; middle={state.synthetic_middle}; physical shift mask={state.physical_shift_keys_down}; physical middle={state.physical_middle_down}" |]

let shutdown () =
    if state.lifecycle <> ShutDown then
        state.lifecycle <- ShutDown
        state.suspensions.Clear()
        state.suspension_cleanup_error <- None
        let ownedSyntheticInput = ViewNavigationState.synthetic_input_owned state

        try
            Command.BeginCommand.RemoveHandler command_began
        with error ->
            Debug.WriteLine $"RhinosCanFly command navigation handler cleanup: {error}"

        callback.Enabled <- false

        match ViewNavigationState.release_all state with
        | Ok() -> ()
        | Error error -> Debug.WriteLine $"RhinosCanFly mouse override shutdown: {error}"

        if ownedSyntheticInput then
            RhinoApp.ReleaseMouseCapture() |> ignore

        FlightKeyboardSuppression.reset flightKeyboard
        ViewNavigationState.set_hook_owns_button state Mouse4 false
        ViewNavigationState.set_hook_owns_button state Mouse5 false

        match remove_keyboard_hook () with
        | Ok() -> ()
        | Error error -> Debug.WriteLine $"RhinosCanFly mouse override keyboard hook: {error}"

        match remove_mouse_hook () with
        | Ok() -> ()
        | Error error -> Debug.WriteLine $"RhinosCanFly mouse override hook: {error}"

        state.poll_timer.Dispose()
