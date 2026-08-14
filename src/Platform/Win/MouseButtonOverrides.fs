module RhinosCanFly.Platform.Win.MouseButtonOverrides

// Rhino 9 deprecates GetViewList(bool, bool), but Rhino 7 has no replacement.
#nowarn "44"

open System
open System.Diagnostics
open System.Drawing
open Rhino
open Rhino.Commands
open Rhino.Display
open Rhino.UI
open RhinosCanFly
open RhinosCanFly.Platform.Win.ViewNavigationTypes

let state = create_state ()
let flightKeyboard = FlightKeyboardSuppression.create ()

[<Struct>]
type NavigationSample =
    { view_serial_number: uint32
      mode: ViewNavigationMode
      point: Point }

type HookState =
    | HookAbsent
    | HookInstalled of Win32Native.WindowsHook
    | HookRemovalPending of hook: Win32Native.WindowsHook * error: string * attempts: int
    | HookRemovalAbandoned of hook: Win32Native.WindowsHook * error: string

[<Literal>]
let maximum_hook_removal_attempts = 8

let mutable keyboard_hook_state = HookAbsent
let mutable mouse_hook_state = HookAbsent

let log_exception (context: string) (error: exn) =
    Debug.WriteLine $"RhinosCanFly {context}: {error}"

let request_ui_redraw () =
    try
        Win32.request_application_redraw (RhinoApp.MainWindowHandle())
    with error ->
        log_exception "UI redraw request" error

type NavigationExitCallback() =
    inherit MouseCallback()

    let mutable leftButtonOwned = false
    let mutable previousNavigationSample: NavigationSample voption = ValueNone

    let active_navigation_mode () =
        if state.lifecycle <> Available then
            ValueNone
        elif ViewNavigationState.side_button_navigation_active state then
            ValueSome Pivot
        else
            match state.view_latch with
            | PivotActive _ -> ValueSome Pivot
            | PanActive _ -> ValueSome Pan
            | NoViewLatch
            | WaitingForRelease _ -> ValueNone

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
                this.Enabled <-
                    state.lifecycle = Available
                    && (ViewNavigationState.left_mouse_exit_enabled state || this.NavigationActive)
        with error ->
            log_exception "mouse override callback" error

    override this.OnMouseUp(event: MouseCallbackEventArgs) =
        try
            if leftButtonOwned && event.MouseButton = Rhino.UI.MouseButton.Left then
                event.Cancel <- true
                leftButtonOwned <- false

                this.Enabled <-
                    state.lifecycle = Available
                    && (ViewNavigationState.left_mouse_exit_enabled state || this.NavigationActive)
        with error ->
            log_exception "mouse override callback" error

    override _.OnMouseMove(event: MouseCallbackEventArgs) =
        try
            match active_navigation_mode () with
            | ValueNone -> previousNavigationSample <- ValueNone
            | ValueSome mode when not (isNull event.View) ->
                let view = event.View

                match ViewNavigationState.navigation_root state with
                | ValueSome expected when ViewNavigationState.root_window view.Handle = expected ->
                    let current =
                        { view_serial_number = view.RuntimeSerialNumber
                          mode = mode
                          point = event.ViewportPoint }

                    match previousNavigationSample with
                    | ValueSome previous when
                        previous.view_serial_number = current.view_serial_number
                        && previous.mode = current.mode
                        ->
                        let changed =
                            match mode with
                            | Pivot -> view.ActiveViewport.MouseRotateAroundTarget(previous.point, current.point)
                            | Pan -> view.ActiveViewport.MouseLateralDolly(previous.point, current.point)

                        if changed then
                            view.Redraw()
                    | ValueSome _
                    | ValueNone -> ()

                    previousNavigationSample <- ValueSome current
                    event.Cancel <- true
                | ValueSome _
                | ValueNone -> previousNavigationSample <- ValueNone
            | ValueSome _ -> previousNavigationSample <- ValueNone
        with error ->
            previousNavigationSample <- ValueNone
            log_exception "direct view navigation" error

    member _.OwnsLeftButton = leftButtonOwned
    member _.NavigationActive = ValueOption.isSome (active_navigation_mode ())
    member _.ResetNavigation() = previousNavigationSample <- ValueNone

let callback = NavigationExitCallback()

let callback_should_be_enabled () =
    ViewNavigationState.left_mouse_exit_enabled state

let refresh_callback_enabled () =
    let shouldEnable =
        callback.OwnsLeftButton
        || (state.lifecycle = Available
            && (callback_should_be_enabled () || callback.NavigationActive))

    if not callback.NavigationActive then
        callback.ResetNavigation()

    if callback.Enabled <> shouldEnable then
        callback.Enabled <- shouldEnable

let try_refresh_callback_enabled () =
    try
        refresh_callback_enabled ()
    with error ->
        log_exception "mouse override callback refresh" error

let handle_keyboard_event (event: Win32.KeyboardHookEvent) =
    let mutable swallow = false

    try
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

        if hookActive && FlightKeyboardSuppression.handle_event event flightKeyboard then
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
        log_exception "mouse override keyboard hook" error
        swallow

let install_keyboard_hook () =
    match keyboard_hook_state with
    | HookInstalled _ -> Ok()
    | HookRemovalPending(_, error, _)
    | HookRemovalAbandoned(_, error) -> Error $"The previous keyboard hook could not be removed: {error}"
    | HookAbsent ->
        match Win32.install_keyboard_hook handle_keyboard_event with
        | Ok hook ->
            keyboard_hook_state <- HookInstalled hook
            Ok()
        | Error error -> Error error

let remove_keyboard_hook () =
    match keyboard_hook_state with
    | HookAbsent -> Ok()
    | HookInstalled hook ->
        match Win32.remove_hook hook with
        | Ok() ->
            keyboard_hook_state <- HookAbsent
            Ok()
        | Error error ->
            keyboard_hook_state <- HookRemovalPending(hook, error, 1)
            ViewNavigationState.keep_watchdog_running state
            Error error
    | HookRemovalPending(hook, _, _)
    | HookRemovalAbandoned(hook, _) ->
        match Win32.remove_hook hook with
        | Ok() ->
            keyboard_hook_state <- HookAbsent
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

            Error error

[<Struct>]
type ViewWindow =
    { serial_number: uint32
      handle: nativeint }

let mutable view_windows: ViewWindow array = Array.empty
let mutable view_create_subscribed = false
let mutable view_destroy_subscribed = false

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
        log_exception "viewport window refresh" error
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
            log_exception "viewport window created" error)

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
            log_exception "viewport window destroyed" error)

let subscribe_view_events () =
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
                Debug.WriteLine $"RhinosCanFly Create subscription rollback: {cleanupError}"

        if view_destroy_subscribed then
            try
                RhinoView.Destroy.RemoveHandler view_destroyed
                view_destroy_subscribed <- false
            with cleanupError ->
                Debug.WriteLine $"RhinosCanFly Destroy subscription rollback: {cleanupError}"

        view_windows <- Array.empty
        raise error

let unsubscribe_view_events () =
    if view_create_subscribed || view_destroy_subscribed then
        let errors = ResizeArray<string>()

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
    | Win32Native.XBUTTON1 -> ValueSome Mouse4
    | Win32Native.XBUTTON2 -> ValueSome Mouse5
    | _ -> ValueNone

let handle_mouse_event (event: Win32.MouseHookEvent) =
    let mutable swallow = false

    try
        match side_button_from_data event.mouse_data with
        | ValueNone -> false
        | ValueSome button ->
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
        log_exception "mouse override hook" error
        swallow

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
                Ok()
            | Error error ->
                unsubscribe_view_events ()
                Error error
        with error ->
            log_exception "mouse-hook installation" error
            Error $"Could not track Rhino viewport windows: {error.Message}"

let remove_mouse_hook () =
    match mouse_hook_state with
    | HookAbsent ->
        try
            unsubscribe_view_events ()
            Ok()
        with error ->
            log_exception "mouse-hook removal" error
            Error $"Could not stop tracking Rhino viewport windows: {error.Message}"
    | HookInstalled hook ->
        match Win32.remove_hook hook with
        | Ok() ->
            mouse_hook_state <- HookAbsent

            try
                unsubscribe_view_events ()
                Ok()
            with error ->
                Error $"Could not stop tracking Rhino viewport windows: {error.Message}"
        | Error error ->
            mouse_hook_state <- HookRemovalPending(hook, error, 1)
            ViewNavigationState.keep_watchdog_running state
            Error error
    | HookRemovalPending(hook, _, _)
    | HookRemovalAbandoned(hook, _) ->
        match Win32.remove_hook hook with
        | Ok() ->
            mouse_hook_state <- HookAbsent

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

            Error error

let mouse_hook_needed () =
    state.lifecycle <> ShutDown
    && (ViewNavigationState.side_button_routing_enabled state
        || ViewNavigationState.hook_owns_any_button state)

let refresh_mouse_hook () =
    if mouse_hook_needed () then
        install_mouse_hook ()
    else
        remove_mouse_hook ()

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
    log_exception "mouse override timer" error

    match ViewNavigationState.release_all state with
    | Ok() -> ()
    | Error cleanupError -> Debug.WriteLine $"RhinosCanFly mouse override timer cleanup: {cleanupError}"

    try_refresh_callback_enabled ()

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

let activate_degraded (error: string) =
    state.lifecycle <- Degraded error

    try
        callback.Enabled <- callback.OwnsLeftButton
    with callbackError ->
        log_exception "mouse override callback disable" callbackError

    try
        apply_poll_requirement ()
    with timerError ->
        log_exception "mouse override recovery timer" timerError

let activate_available () =
    try
        refresh_callback_enabled ()
        apply_poll_requirement ()
        state.lifecycle <- Available
        Ok()
    with error ->
        let message = $"Could not activate mouse button overrides: {error.Message}"
        activate_degraded message
        Error message

let poll_timer_elapsed () =
    try
        let navigationWasActive =
            ViewNavigationState.any_button_engaged state
            || ViewNavigationState.view_latch_engaged state

        try
            SideButtonTransitions.process_hook_events state
            prune_released_side_buttons ()

            let foreground = ViewNavigationState.foreground_root_window ()

            let navigationLostFocus =
                match ViewNavigationState.navigation_root state with
                | ValueSome expected -> foreground <> expected
                | ValueNone -> false

            if navigationLostFocus then
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

            refresh_callback_enabled ()

            if
                navigationWasActive
                && not (ViewNavigationState.any_button_engaged state)
                && not (ViewNavigationState.view_latch_engaged state)
            then
                request_ui_redraw ()

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
            let keyboardResult = install_keyboard_hook ()
            let mouseResult = refresh_mouse_hook ()

            match keyboardResult with
            | Ok() -> ()
            | Error error -> Debug.WriteLine $"RhinosCanFly mouse override keyboard hook: {error}"

            match mouseResult with
            | Ok() -> ()
            | Error error -> Debug.WriteLine $"RhinosCanFly mouse override hook: {error}"

            match state.lifecycle, keyboardResult, mouseResult with
            | Degraded _, Ok(), Ok() when state.suspension_ids.Count = 0 ->
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

state.poll_timer.Tick.Add(fun (_: EventArgs) -> poll_timer_elapsed ())

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
                    || ViewNavigationState.any_button_engaged state
                    || ViewNavigationState.view_latch_engaged state)
            then
                match ViewNavigationState.release_all state with
                | Ok() -> ()
                | Error error -> Debug.WriteLine $"RhinosCanFly command navigation cleanup: {error}"

                try_refresh_callback_enabled ()
                apply_poll_requirement ()
                request_ui_redraw ()
        with error ->
            log_exception "command navigation callback" error)

do Command.BeginCommand.AddHandler command_began

let suppress_flight_keyboard (bindings: FlightBindings) =
    FlightKeyboardSuppression.start bindings flightKeyboard

    match install_keyboard_hook () with
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

let start_view_latch (window: RootWindow) (mode: ViewNavigationMode) (completion: Action option) =
    match install_keyboard_hook () with
    | Error error -> Error error
    | Ok() -> ViewLatchTransitions.start_or_switch state window mode completion

let stop_view_latch (mode: ViewNavigationMode) =
    let wasActive = ViewLatchTransitions.is_mode state mode
    let result = ViewLatchTransitions.stop state mode
    try_refresh_callback_enabled ()

    if wasActive && not (ViewNavigationState.view_latch_engaged state) then
        request_ui_redraw ()

    result

let view_latch_is (mode: ViewNavigationMode) = ViewLatchTransitions.is_mode state mode

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

                    match refresh_mouse_hook () with
                    | Error error ->
                        activate_degraded error
                        Error error
                    | Ok() ->
                        match install_keyboard_hook () with
                        | Error error ->
                            activate_degraded error
                            Error error
                        | Ok() -> activate_available ()
                with error ->
                    let message = $"Could not apply mouse button overrides: {error.Message}"
                    log_exception "mouse override configuration" error
                    activate_degraded message
                    Error message

let suspend () =
    if state.lifecycle = ShutDown then
        Error "Mouse button overrides have already shut down."
    elif state.suspension_ids.Count > 0 then
        state.next_suspension_id <- state.next_suspension_id + 1L

        let lease =
            { id = state.next_suspension_id
              cleanup_error = state.suspension_cleanup_error }

        state.suspension_ids.Add lease.id |> ignore
        Ok lease
    else
        let errors = ResizeArray<string>()
        state.lifecycle <- Suspended

        try
            callback.Enabled <- callback.OwnsLeftButton
        with error ->
            log_exception "mouse override callback suspension" error
            errors.Add error.Message

        try
            match ViewNavigationState.release_all state with
            | Ok() -> ()
            | Error error -> errors.Add error
        with error ->
            log_exception "mouse override suspension cleanup" error
            errors.Add error.Message

        try
            apply_poll_requirement ()
        with error ->
            log_exception "mouse override suspension timer" error
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
              cleanup_error = cleanupError }

        state.suspension_ids.Add lease.id |> ignore
        Ok lease

let resume (lease: InputSuspensionLease) =
    if state.lifecycle = ShutDown then
        Error "Mouse button overrides have already shut down."
    elif not (state.suspension_ids.Remove lease.id) then
        Ok()
    elif state.suspension_ids.Count > 0 then
        Ok()
    else
        state.suspension_cleanup_error <- None
        state.lifecycle <- Resuming

        try
            refresh_callback_enabled ()

            match refresh_mouse_hook () with
            | Error error ->
                activate_degraded error
                Error error
            | Ok() ->
                match install_keyboard_hook () with
                | Error error ->
                    activate_degraded error
                    Error error
                | Ok() -> activate_available ()
        with error ->
            let message = $"Could not resume mouse button overrides: {error.Message}"
            log_exception "mouse override resume" error
            activate_degraded message
            Error message

let retry_hook_cleanup () =
    let errors = ResizeArray<string>()

    let attempt (name: string) (action: unit -> unit) =
        try
            action ()
        with error ->
            log_exception $"mouse override recovery {name}" error
            errors.Add $"{name}: {error.Message}"

    match ViewNavigationState.release_all state with
    | Ok() -> ()
    | Error error -> errors.Add $"view navigation: {error}"

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

    match install_keyboard_hook () with
    | Ok() -> ()
    | Error error -> errors.Add $"keyboard hook: {error}"

    match refresh_mouse_hook () with
    | Ok() -> ()
    | Error error -> errors.Add $"mouse hook: {error}"

    attempt "timer" apply_poll_requirement

    if state.lifecycle <> ShutDown && state.suspension_ids.Count = 0 then
        if errors.Count = 0 then
            match activate_available () with
            | Ok() -> ()
            | Error error -> errors.Add error
        else
            activate_degraded (String.concat "; " errors)

    List.ofSeq errors

let shutdown () =
    if state.lifecycle <> ShutDown then
        let attempt (name: string) (action: unit -> unit) =
            try
                action ()
            with error ->
                log_exception $"mouse override {name} shutdown" error

        state.lifecycle <- ShutDown
        state.suspension_ids.Clear()
        state.suspension_cleanup_error <- None

        attempt "command handler" (fun () -> Command.BeginCommand.RemoveHandler command_began)
        attempt "callback" (fun () -> callback.Enabled <- false)

        attempt "view navigation" (fun () ->
            match ViewNavigationState.release_all state with
            | Ok() -> ()
            | Error error -> Debug.WriteLine $"RhinosCanFly mouse override shutdown: {error}")

        attempt "keyboard state" (fun () -> FlightKeyboardSuppression.reset flightKeyboard)

        attempt "side-button ownership" (fun () ->
            ViewNavigationState.set_hook_owns_button state Mouse4 false
            ViewNavigationState.set_hook_owns_button state Mouse5 false)

        attempt "keyboard hook" (fun () ->
            match remove_keyboard_hook () with
            | Ok() -> ()
            | Error error -> failwith error)

        attempt "mouse hook" (fun () ->
            match remove_mouse_hook () with
            | Ok() -> ()
            | Error error -> failwith error)

        attempt "timer" (fun () -> state.poll_timer.Dispose())
