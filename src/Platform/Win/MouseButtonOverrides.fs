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
let mutable standalone_latch_hook_used = false

let record_exception (context: string) (error: exn) =
    InputDiagnostics.record_exception context error

type NavigationExitCallback() =
    inherit MouseCallback()

    let mutable leftButtonOwned = false

    override this.OnMouseDown(event: MouseCallbackEventArgs) =
        try
            if event.MouseButton = Rhino.UI.MouseButton.Left then
                // A focus change can hide the matching Up from Rhino. A fresh
                // Down starts a new pair, so do not carry stale ownership into it.
                leftButtonOwned <- false

            let exitsNavigation =
                state.lifecycle = Available
                && ViewNavigationState.left_mouse_exit_enabled state
                && (state.routing.exit_on_mouse_left || ViewNavigationState.exit_key_down state)
                && event.MouseButton = Rhino.UI.MouseButton.Left
                && (ViewNavigationState.any_button_engaged state
                    || ViewNavigationState.view_latch_engaged state)

            if exitsNavigation then
                event.Cancel <- true
                leftButtonOwned <- true
                state.navigation_exit_requested <- true
                ViewNavigationState.keep_timer_running state
            elif event.MouseButton = Rhino.UI.MouseButton.Left then
                this.Enabled <- state.lifecycle = Available && ViewNavigationState.left_mouse_exit_enabled state
        with error ->
            record_exception "NavigationExitCallback.OnMouseDown" error
            Debug.WriteLine $"RhinosCanFly mouse override callback: {error.Message}"

    override this.OnMouseUp(event: MouseCallbackEventArgs) =
        try
            if leftButtonOwned && event.MouseButton = Rhino.UI.MouseButton.Left then
                event.Cancel <- true
                leftButtonOwned <- false

                this.Enabled <- state.lifecycle = Available && ViewNavigationState.left_mouse_exit_enabled state
        with error ->
            record_exception "NavigationExitCallback.OnMouseUp" error
            Debug.WriteLine $"RhinosCanFly mouse override callback: {error.Message}"

    member _.OwnsLeftButton = leftButtonOwned

let callback = NavigationExitCallback()

let callback_should_be_enabled () =
    ViewNavigationState.left_mouse_exit_enabled state

let refresh_callback_enabled () =
    callback.Enabled <-
        callback.OwnsLeftButton
        || (state.lifecycle = Available && callback_should_be_enabled ())

let handle_keyboard_event (event: Win32.KeyboardHookEvent) =
    let mutable swallow = false

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
            true
        elif
            not event.released
            && state.lifecycle = Available
            && (ViewNavigationState.any_button_engaged state
                || ViewNavigationState.view_latch_engaged state)
            && ViewNavigationState.exit_binding_down_for_event state event.physical_key
        then
            state.navigation_exit_requested <- true
            ViewNavigationState.keep_timer_running state

            if event.was_down then
                false
            else
                FlightKeyboardSuppression.suppress_key_down event.physical_key flightKeyboard
                swallow <- true
                true
        else
            false
    with error ->
        record_exception "MouseButtonOverrides.handle_keyboard_event" error
        Debug.WriteLine $"RhinosCanFly mouse override keyboard hook: {error.Message}"
        swallow

let install_keyboard_hook (reason: InputDiagnostics.HookOperationReason) =
    match keyboard_hook_state with
    | HookInstalled _ -> Ok()
    | HookRemovalPending(_, error, _)
    | HookRemovalAbandoned(_, error) -> Error $"The previous keyboard hook could not be removed: {error}"
    | HookAbsent ->
        let started =
            InputDiagnostics.hook_operation_begin InputDiagnostics.HookKind.Keyboard reason

        let result =
            try
                Win32.install_keyboard_hook handle_keyboard_event
            finally
                InputDiagnostics.hook_operation_end InputDiagnostics.HookKind.Keyboard reason started

        match result with
        | Ok hook ->
            keyboard_hook_state <- HookInstalled hook
            InputDiagnostics.record InputDiagnostics.EventKind.HookInstalled 1L 0L
            Ok()
        | Error error -> Error error

let remove_keyboard_hook (reason: InputDiagnostics.HookOperationReason) =
    match keyboard_hook_state with
    | HookAbsent -> Ok()
    | HookInstalled hook ->
        let started =
            InputDiagnostics.hook_removal_begin InputDiagnostics.HookKind.Keyboard reason

        let result =
            try
                Win32.remove_hook hook
            finally
                InputDiagnostics.hook_removal_end InputDiagnostics.HookKind.Keyboard reason started

        match result with
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
        let started =
            InputDiagnostics.hook_removal_begin InputDiagnostics.HookKind.Keyboard reason

        let result =
            try
                Win32.remove_hook hook
            finally
                InputDiagnostics.hook_removal_end InputDiagnostics.HookKind.Keyboard reason started

        match result with
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

[<Struct>]
type ViewWindow =
    { serial_number: uint32
      handle: nativeint }

let mutable view_windows: ViewWindow array = Array.empty
let mutable view_create_subscribed = false
let mutable view_destroy_subscribed = false

let view_events_subscribed () =
    view_create_subscribed && view_destroy_subscribed

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

        Ok()
    with error ->
        view_windows <- Array.empty
        record_exception "MouseButtonOverrides.refresh_view_windows" error
        Debug.WriteLine $"RhinosCanFly viewport window refresh: {error.Message}"
        Error $"Could not enumerate Rhino viewport windows: {error.Message}"

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
            record_exception "MouseButtonOverrides.view_created" error
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
            record_exception "MouseButtonOverrides.view_destroyed" error
            Debug.WriteLine $"RhinosCanFly viewport window destroyed: {error.Message}")

let subscribe_view_events () =
    let started = InputDiagnostics.view_subscription_begin true

    try
        try
            if not view_create_subscribed then
                RhinoView.Create.AddHandler view_created
                view_create_subscribed <- true

            if not view_destroy_subscribed then
                RhinoView.Destroy.AddHandler view_destroyed
                view_destroy_subscribed <- true

            match refresh_view_windows () with
            | Ok() -> ()
            | Error error -> failwith error
        with error ->
            if view_create_subscribed then
                try
                    RhinoView.Create.RemoveHandler view_created
                    view_create_subscribed <- false
                with cleanupError ->
                    record_exception "MouseButtonOverrides Create subscription rollback" cleanupError

            if view_destroy_subscribed then
                try
                    RhinoView.Destroy.RemoveHandler view_destroyed
                    view_destroy_subscribed <- false
                with cleanupError ->
                    record_exception "MouseButtonOverrides Destroy subscription rollback" cleanupError

            view_windows <- Array.empty

            raise error
    finally
        InputDiagnostics.view_subscription_end true started

let unsubscribe_view_events () =
    if view_create_subscribed || view_destroy_subscribed then
        let started = InputDiagnostics.view_subscription_begin false
        let errors = ResizeArray<string>()

        try
            if view_create_subscribed then
                try
                    RhinoView.Create.RemoveHandler view_created
                    view_create_subscribed <- false
                with error ->
                    errors.Add $"Create: {error.Message}"

            if view_destroy_subscribed then
                try
                    RhinoView.Destroy.RemoveHandler view_destroyed
                    view_destroy_subscribed <- false
                with error ->
                    errors.Add $"Destroy: {error.Message}"

            view_windows <- Array.empty

            if errors.Count > 0 then
                failwith (String.concat "; " errors)
        finally
            InputDiagnostics.view_subscription_end false started

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
    let mutable swallow = false

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
        elif injected then
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

                if
                    isDown
                    && ViewNavigationState.hook_button_ownership state button = ReleaseObserved
                then
                    // The previous Up happened outside Rhino and was observed
                    // by the watchdog. This Down starts a new physical pair.
                    ViewNavigationState.set_hook_owns_button state button false

                let hookOwnsButton = ViewNavigationState.hook_owns_button state button

                if isUp && hookOwnsButton then
                    swallow <- true
                    ViewNavigationState.set_hook_owns_button state button false

                    if state.lifecycle = Available then
                        state.pending_side_button_events.Enqueue(ButtonUp button)

                    ViewNavigationState.keep_timer_running state
                    true
                elif isDown && hookOwnsButton then
                    swallow <- true
                    true
                elif
                    not hookActive
                    || state.lifecycle <> Available
                    || ViewNavigationState.mode_for state button = Disabled
                then
                    false
                elif isDown then
                    match try_view_root event.hook_window with
                    | ValueSome hookRoot ->
                        match try_view_root event.point_window with
                        | ValueSome pointRoot when
                            hookRoot = pointRoot && hookRoot = ViewNavigationState.foreground_root_window ()
                            ->
                            swallow <- true
                            ViewNavigationState.set_hook_owns_button state button true
                            state.pending_side_button_events.Enqueue(ButtonDown(button, hookRoot))
                            ViewNavigationState.keep_timer_running state
                            true
                        | ValueSome _
                        | ValueNone -> false
                    | ValueNone -> false
                else
                    false
    with error ->
        record_exception "MouseButtonOverrides.handle_mouse_event" error
        Debug.WriteLine $"RhinosCanFly mouse override hook: {error.Message}"
        swallow

let install_mouse_hook (reason: InputDiagnostics.HookOperationReason) =
    match mouse_hook_state with
    | HookInstalled _ -> Ok()
    | HookRemovalPending(_, error, _)
    | HookRemovalAbandoned(_, error) -> Error $"The previous mouse hook could not be removed: {error}"
    | HookAbsent ->
        try
            subscribe_view_events ()

            let started =
                InputDiagnostics.hook_operation_begin InputDiagnostics.HookKind.Mouse reason

            let result =
                try
                    Win32.install_mouse_hook handle_mouse_event
                finally
                    InputDiagnostics.hook_operation_end InputDiagnostics.HookKind.Mouse reason started

            match result with
            | Ok hook ->
                mouse_hook_state <- HookInstalled hook
                InputDiagnostics.record InputDiagnostics.EventKind.HookInstalled 2L 0L
                Ok()
            | Error error ->
                unsubscribe_view_events ()
                Error error
        with error ->
            record_exception "MouseButtonOverrides.install_mouse_hook" error
            Error $"Could not track Rhino viewport windows: {error.Message}"

let remove_mouse_hook (reason: InputDiagnostics.HookOperationReason) =
    match mouse_hook_state with
    | HookAbsent ->
        try
            unsubscribe_view_events ()
            Ok()
        with error ->
            record_exception "MouseButtonOverrides.remove_mouse_hook" error
            Error $"Could not stop tracking Rhino viewport windows: {error.Message}"
    | HookInstalled hook ->
        let started =
            InputDiagnostics.hook_removal_begin InputDiagnostics.HookKind.Mouse reason

        let result =
            try
                Win32.remove_hook hook
            finally
                InputDiagnostics.hook_removal_end InputDiagnostics.HookKind.Mouse reason started

        match result with
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
        let started =
            InputDiagnostics.hook_removal_begin InputDiagnostics.HookKind.Mouse reason

        let result =
            try
                Win32.remove_hook hook
            finally
                InputDiagnostics.hook_removal_end InputDiagnostics.HookKind.Mouse reason started

        match result with
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

let mouse_hook_needed () =
    state.lifecycle <> ShutDown
    && (standalone_latch_hook_used
        || ViewNavigationState.side_button_routing_enabled state
        || Option.isSome state.routing.shift_right_click
        || Option.isSome state.routing.alt_right_click
        || ViewNavigationState.hook_owns_any_button state)

let refresh_mouse_hook (reason: InputDiagnostics.HookOperationReason) =
    if mouse_hook_needed () then
        install_mouse_hook reason
    else
        remove_mouse_hook reason

let mouse_hook_needs_reconciliation () =
    match mouse_hook_state with
    | HookAbsent -> mouse_hook_needed ()
    | HookInstalled _ -> not (mouse_hook_needed ())
    | HookRemovalPending _ -> true
    | HookRemovalAbandoned _ -> false

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
    record_exception "MouseButtonOverrides.poll_timer" error
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

let hook_removal_abandoned () =
    match keyboard_hook_state, mouse_hook_state with
    | HookRemovalAbandoned _, _
    | _, HookRemovalAbandoned _ -> true
    | _ -> false

let poll_requirement () =
    if ViewNavigationState.fast_poll_required state then
        PollFast
    elif
        (match state.lifecycle with
         | Degraded _ -> not (hook_removal_abandoned ())
         | Available
         | Suspended
         | Resuming
         | ShutDown -> false)
        || ViewNavigationState.hook_owns_any_button state
        || ViewNavigationState.synthetic_input_owned state
        || Option.isSome state.pending_view_completion
        || hook_removal_pending ()
        || mouse_hook_needs_reconciliation ()
    then
        PollWatchdog
    else
        PollStopped

let apply_poll_requirement () =
    match poll_requirement () with
    | PollFast -> ViewNavigationState.keep_timer_running state
    | PollWatchdog -> ViewNavigationState.keep_watchdog_running state
    | PollStopped ->
        if state.poll_timer.Enabled then
            state.poll_timer.Stop()
            InputDiagnostics.record InputDiagnostics.EventKind.TimerStopped 0L 0L

let activate_degraded (error: string) =
    state.lifecycle <- Degraded error

    try
        callback.Enabled <- callback.OwnsLeftButton
    with callbackError ->
        record_exception "MouseButtonOverrides.disable_callback" callbackError

    try
        apply_poll_requirement ()
    with timerError ->
        record_exception "MouseButtonOverrides.degraded_timer" timerError

let activate_available () =
    try
        callback.Enabled <- callback.OwnsLeftButton || callback_should_be_enabled ()
        apply_poll_requirement ()
        state.lifecycle <- Available
        Ok()
    with error ->
        let message = $"Could not activate mouse button overrides: {error.Message}"
        activate_degraded message
        Error message

let poll_timer_elapsed () =
    try
        try
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

            SideButtonTransitions.process_hook_events state
            prune_released_side_buttons ()

            let foreground = ViewNavigationState.foreground_root_window ()

            let navigationLostFocus =
                match ViewNavigationState.navigation_root state with
                | ValueSome expected -> foreground <> expected
                | ValueNone -> false

            if navigationLostFocus then
                let ownedSyntheticInput = ViewNavigationState.synthetic_input_owned state

                match ViewNavigationState.release_all_after_focus_loss state with
                | Ok() -> ()
                | Error error -> Debug.WriteLine $"RhinosCanFly mouse override focus loss: {error}"

                if ownedSyntheticInput then
                    try
                        RhinoApp.ReleaseMouseCapture() |> ignore
                        InputDiagnostics.record InputDiagnostics.EventKind.ViewCaptureReleased 0L 0L
                    with error ->
                        record_exception "MouseButtonOverrides.focus_loss capture" error
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

        let recoverHooks =
            hook_removal_pending ()
            || mouse_hook_needs_reconciliation ()
            || match state.lifecycle with
               | Degraded _ -> not (hook_removal_abandoned ())
               | Available
               | Suspended
               | Resuming
               | ShutDown -> false

        if recoverHooks then
            let keyboardResult = install_keyboard_hook InputDiagnostics.HookOperationReason.Poll
            let mouseResult = refresh_mouse_hook InputDiagnostics.HookOperationReason.Poll

            match keyboardResult with
            | Ok() -> ()
            | Error error -> Debug.WriteLine $"RhinosCanFly mouse override keyboard hook: {error}"

            match mouseResult with
            | Ok() -> ()
            | Error error -> Debug.WriteLine $"RhinosCanFly mouse override hook: {error}"

            match state.lifecycle, keyboardResult, mouseResult with
            | Degraded _, Ok(), Ok() when state.suspensions.Count = 0 ->
                match activate_available () with
                | Ok() -> ()
                | Error error -> Debug.WriteLine $"RhinosCanFly mouse override activation: {error}"
            | _ -> apply_poll_requirement ()
        else
            apply_poll_requirement ()
    with error ->
        release_after_timer_error error

        try
            apply_poll_requirement ()
        with stopError ->
            Debug.WriteLine $"RhinosCanFly mouse override timer scheduling: {stopError}"

state.poll_timer.Tick.Add(fun (_: EventArgs) ->
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
                    try
                        RhinoApp.ReleaseMouseCapture() |> ignore
                        InputDiagnostics.record InputDiagnostics.EventKind.ViewCaptureReleased 0L 0L
                    with error ->
                        record_exception "MouseButtonOverrides.command capture release" error
                        Debug.WriteLine $"RhinosCanFly command mouse capture cleanup: {error.Message}"

                apply_poll_requirement ()
        with error ->
            record_exception "MouseButtonOverrides.command_began" error
            Debug.WriteLine $"RhinosCanFly command navigation callback: {error}")

do Command.BeginCommand.AddHandler command_began

let suppress_flight_keyboard (bindings: FlightBindings) =
    FlightKeyboardSuppression.start bindings flightKeyboard

    match install_keyboard_hook InputDiagnostics.HookOperationReason.Flight with
    | Ok() -> Ok()
    | Error error ->
        FlightKeyboardSuppression.stop flightKeyboard
        Error error

let release_flight_keyboard () =
    FlightKeyboardSuppression.stop flightKeyboard
    Ok()

let right_click_enabled () =
    ViewLatchTransitions.right_click_enabled state

let handle_right_click (window: RootWindow) =
    ViewLatchTransitions.handle_right_click state window

let start_view_latch (window: RootWindow) (mode: ViewLatchMode) (completion: Action option) =
    match install_keyboard_hook InputDiagnostics.HookOperationReason.Navigation with
    | Error error -> Error error
    | Ok() ->
        match install_mouse_hook InputDiagnostics.HookOperationReason.Navigation with
        | Error error -> Error error
        | Ok() ->
            standalone_latch_hook_used <- true
            ViewLatchTransitions.start_or_switch state window mode completion

let stop_view_latch (mode: ViewLatchMode) = ViewLatchTransitions.stop state mode

let view_latch_is (mode: ViewLatchMode) = ViewLatchTransitions.is_mode state mode

let apply (config: MouseOverrideConfig) =
    if state.lifecycle = ShutDown then
        Error "Mouse button overrides have already shut down."
    else
        match ViewNavigationState.release_all state with
        | Error error ->
            activate_degraded error
            Error error
        | Ok() ->
            state.routing <-
                { mouse4 = SideButtonTransitions.configured_mode config.mouse4
                  mouse5 = SideButtonTransitions.configured_mode config.mouse5
                  shift_right_click = ViewLatchTransitions.configured_mode config.shift_right_click
                  alt_right_click = ViewLatchTransitions.configured_mode config.alt_right_click
                  exit = config.exit_binding
                  exit_on_mouse_left = config.exit_on_left
                  exit_on_mouse_right = config.exit_on_right }

            if state.lifecycle = Suspended then
                Ok()
            else
                state.lifecycle <- Resuming

                try
                    refresh_callback_enabled ()

                    match refresh_mouse_hook InputDiagnostics.HookOperationReason.Configuration with
                    | Error error ->
                        activate_degraded error
                        Error error
                    | Ok() ->
                        match install_keyboard_hook InputDiagnostics.HookOperationReason.Configuration with
                        | Error error ->
                            activate_degraded error
                            Error error
                        | Ok() -> activate_available ()
                with error ->
                    let message = $"Could not apply mouse button overrides: {error.Message}"
                    record_exception "MouseButtonOverrides.apply" error
                    activate_degraded message
                    Error message

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
        let ownedSyntheticInput = ViewNavigationState.synthetic_input_owned state
        let errors = ResizeArray<string>()
        state.lifecycle <- Suspended

        try
            callback.Enabled <- callback.OwnsLeftButton
        with error ->
            record_exception "MouseButtonOverrides.suspend callback" error
            errors.Add error.Message

        try
            match ViewNavigationState.release_all state with
            | Ok() -> ()
            | Error error -> errors.Add error
        with error ->
            record_exception "MouseButtonOverrides.suspend release" error
            errors.Add error.Message

        if ownedSyntheticInput then
            try
                RhinoApp.ReleaseMouseCapture() |> ignore
                InputDiagnostics.record InputDiagnostics.EventKind.ViewCaptureReleased 0L 0L
            with error ->
                record_exception "MouseButtonOverrides.suspend capture" error
                errors.Add error.Message

        try
            apply_poll_requirement ()
        with error ->
            record_exception "MouseButtonOverrides.suspend timer" error
            errors.Add error.Message

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
        state.lifecycle <- Resuming

        try
            refresh_callback_enabled ()

            match refresh_mouse_hook InputDiagnostics.HookOperationReason.Configuration with
            | Error error ->
                activate_degraded error
                Error error
            | Ok() ->
                match install_keyboard_hook InputDiagnostics.HookOperationReason.Configuration with
                | Error error ->
                    activate_degraded error
                    Error error
                | Ok() -> activate_available ()
        with error ->
            let message = $"Could not resume mouse button overrides: {error.Message}"
            record_exception "MouseButtonOverrides.resume" error
            activate_degraded message
            Error message

let hook_state_name (hookState: HookState) =
    match hookState with
    | HookAbsent -> "absent"
    | HookInstalled _ -> "installed"
    | HookRemovalPending _ -> "removal pending"
    | HookRemovalAbandoned(_, error) -> $"removal abandoned ({error})"

let retry_hook_cleanup () =
    let errors = ResizeArray<string>()
    let ownedSyntheticInput = ViewNavigationState.synthetic_input_owned state

    let attempt (name: string) (action: unit -> unit) =
        try
            action ()
        with error ->
            record_exception $"MouseButtonOverrides.recovery {name}" error
            errors.Add $"{name}: {error.Message}"

    match ViewNavigationState.release_all state with
    | Ok() -> ()
    | Error error -> errors.Add $"synthetic input: {error}"

    if ownedSyntheticInput then
        attempt "mouse capture" (fun () ->
            RhinoApp.ReleaseMouseCapture() |> ignore
            InputDiagnostics.record InputDiagnostics.EventKind.ViewCaptureReleased 0L 0L)

    match keyboard_hook_state with
    | HookRemovalPending _
    | HookRemovalAbandoned _ ->
        match remove_keyboard_hook InputDiagnostics.HookOperationReason.Recovery with
        | Ok() -> ()
        | Error error -> errors.Add $"keyboard hook: {error}"
    | HookAbsent
    | HookInstalled _ -> ()

    match mouse_hook_state with
    | HookRemovalPending _
    | HookRemovalAbandoned _ ->
        match remove_mouse_hook InputDiagnostics.HookOperationReason.Recovery with
        | Ok() -> ()
        | Error error -> errors.Add $"mouse hook: {error}"
    | HookAbsent
    | HookInstalled _ -> ()

    match install_keyboard_hook InputDiagnostics.HookOperationReason.Recovery with
    | Ok() -> ()
    | Error error -> errors.Add $"keyboard hook: {error}"

    match refresh_mouse_hook InputDiagnostics.HookOperationReason.Recovery with
    | Ok() -> ()
    | Error error -> errors.Add $"mouse hook: {error}"

    attempt "timer" apply_poll_requirement

    if state.lifecycle <> ShutDown && state.suspensions.Count = 0 then
        if errors.Count = 0 then
            match activate_available () with
            | Ok() -> ()
            | Error error -> errors.Add error
        else
            activate_degraded (String.concat "; " errors)

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

    let (RootWindow foregroundRoot) = ViewNavigationState.foreground_root_window ()

    [| $"Lifecycle: {state.lifecycle}"
       $"Suspensions: {state.suspensions.Count} ({suspensionReasons})"
       $"Suspension cleanup error: {suspensionCleanupError}"
       $"Routing: mouse4={state.routing.mouse4}; mouse5={state.routing.mouse5}; shift+right={state.routing.shift_right_click}; alt+right={state.routing.alt_right_click}"
       $"Foreground root: {foregroundRoot}; tracked views={view_windows.Length}; view events subscribed={view_events_subscribed ()}"
       $"Timer: started={state.poll_timer.Enabled}; interval={float state.poll_timer.Interval / 1000.:F3}s; requirement={poll_requirement ()}"
       $"Timer callbacks: count={state.poll_callback_count}; last={ticksToMilliseconds state.last_poll_duration_ticks:F3} ms; max={ticksToMilliseconds state.maximum_poll_duration_ticks:F3} ms"
       $"Keyboard hook: {hook_state_name keyboard_hook_state}; active={FlightKeyboardSuppression.is_active flightKeyboard}; pending releases={flightKeyboard.suppressed_keys_down.Count}"
       $"Mouse hook: {hook_state_name mouse_hook_state}; owned buttons={ViewNavigationState.hook_owns_any_button state}; queued events={state.pending_side_button_events.Count}"
       $"Navigation: buttons={ViewNavigationState.any_button_engaged state}; latch={state.view_latch}; left button owned={callback.OwnsLeftButton}; pending release root={state.pending_synthetic_release_root}; pending completion={Option.isSome state.pending_view_completion}; synthetic shift={state.synthetic_shift}; middle={state.synthetic_middle}; physical shift mask={state.physical_shift_keys_down}; physical middle={state.physical_middle_down}" |]

let shutdown () =
    if state.lifecycle <> ShutDown then
        let attempt (name: string) (action: unit -> unit) =
            try
                action ()
            with error ->
                record_exception $"MouseButtonOverrides.shutdown {name}" error
                Debug.WriteLine $"RhinosCanFly mouse override {name} shutdown: {error.Message}"

        state.lifecycle <- ShutDown
        state.suspensions.Clear()
        state.suspension_cleanup_error <- None
        let ownedSyntheticInput = ViewNavigationState.synthetic_input_owned state

        attempt "command handler" (fun () -> Command.BeginCommand.RemoveHandler command_began)
        attempt "callback" (fun () -> callback.Enabled <- false)

        attempt "synthetic input" (fun () ->
            match ViewNavigationState.release_all state with
            | Ok() -> ()
            | Error error -> Debug.WriteLine $"RhinosCanFly mouse override shutdown: {error}")

        if ownedSyntheticInput then
            attempt "mouse capture" (fun () -> RhinoApp.ReleaseMouseCapture() |> ignore)

        attempt "forced synthetic input" (fun () ->
            match ViewNavigationState.force_release_synthetic_input state with
            | Ok() -> ()
            | Error error -> failwith error)

        attempt "view completion" (fun () ->
            match ViewNavigationState.complete_pending_view_latch state with
            | Ok() -> ()
            | Error error -> failwith error)

        attempt "keyboard state" (fun () -> FlightKeyboardSuppression.reset flightKeyboard)

        attempt "side-button ownership" (fun () ->
            ViewNavigationState.set_hook_owns_button state Mouse4 false
            ViewNavigationState.set_hook_owns_button state Mouse5 false)

        attempt "keyboard hook" (fun () ->
            match remove_keyboard_hook InputDiagnostics.HookOperationReason.Shutdown with
            | Ok() -> ()
            | Error error -> failwith error)

        attempt "mouse hook" (fun () ->
            match remove_mouse_hook InputDiagnostics.HookOperationReason.Shutdown with
            | Ok() -> ()
            | Error error -> failwith error)

        attempt "timer" (fun () -> state.poll_timer.Dispose())
