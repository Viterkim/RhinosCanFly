module RhinosCanFly.PlatformMouseActions

open System
open System.Diagnostics
open System.Drawing
open Rhino
open Rhino.ApplicationSettings
open Rhino.Commands
open Rhino.Display
open RhinosCanFly
open RhinosCanFly.Platform.Win
open RhinosCanFly.Platform.Win.MouseOverrideTypes

let state = create_state ()
let right_click = RightClickTransitions.create ()
let mutable hook_ui_wake: PlatformInputWake.State option = None
let mutable hook_ui_main_loop_handler: EventHandler option = None
let hook_ui_work_requested = Event<unit>()
let mutable suspended_hook_events = 0L
let mutable suspended_hook_right_down = 0L
let mutable suspended_hook_right_up = 0L
let mutable suspended_hook_other_buttons = 0L

let signal_hook_ui_work () =
    match hook_ui_wake with
    | Some wake -> PlatformInputWake.signal wake
    | None -> ()

let install_hook_ui_wake () =
    match hook_ui_wake with
    | Some _ -> Ok()
    | None ->
        try
            let wake = PlatformInputWake.create (RootWindow(RhinoApp.MainWindowHandle()))

            try
                let handler =
                    EventHandler(fun (_: obj) (_: EventArgs) ->
                        if PlatformInputWake.acknowledge_if_pending wake then
                            hook_ui_work_requested.Trigger())

                RhinoApp.MainLoop.AddHandler handler
                hook_ui_wake <- Some wake
                hook_ui_main_loop_handler <- Some handler
                Ok()
            with error ->
                PlatformInputWake.dispose wake
                Error $"Could not install the mouse-action UI wake: {error.Message}"
        with error ->
            Error $"Could not install the mouse-action UI wake: {error.Message}"

let remove_hook_ui_wake () =
    let errors = ResizeArray<string>()

    match hook_ui_main_loop_handler with
    | Some handler ->
        try
            RhinoApp.MainLoop.RemoveHandler handler
            hook_ui_main_loop_handler <- None
        with error ->
            errors.Add $"main-loop handler: {error.Message}"
    | None -> ()

    if errors.Count = 0 then
        match hook_ui_wake with
        | Some wake ->
            PlatformInputWake.dispose wake
            hook_ui_wake <- None
        | None -> ()

    if errors.Count = 0 then
        Ok()
    else
        Error(String.concat "; " errors)

let request_navigation_exit () =
    state.navigation_exit_requested <- true
    MouseOverrideState.keep_timer_running state

let mouse_hook = MouseHook.create ()
let mutable command_depth = if Command.InCommand() then 1 else 0

let log_exception (context: string) (error: exn) =
    Debug.WriteLine $"RhinosCanFly {context}: {error}"

let raw_navigation =
    RawNavigationCoordinator.create state right_click request_navigation_exit log_exception

let viewport_registry =
    ViewportRegistry.create
        { hook_installed = fun () -> MouseHook.installed mouse_hook
          ensure_ui_wake =
            fun () ->
                match install_hook_ui_wake () with
                | Ok() -> ()
                | Error error -> Debug.WriteLine $"RhinosCanFly mouse-action UI wake: {error}"
          active_navigation_host = fun () -> RawNavigationCoordinator.active_host raw_navigation
          request_navigation_exit = request_navigation_exit
          log_exception = log_exception }

let request_ui_redraw () =
    try
        Win32.request_application_redraw (RhinoApp.MainWindowHandle())
    with error ->
        log_exception "UI redraw request" error

let side_button_from_data (mouseData: uint32) =
    match mouseData >>> 16 with
    | Win32Native.XBUTTON1 -> ValueSome Mouse4
    | Win32Native.XBUTTON2 -> ValueSome Mouse5
    | _ -> ValueNone

let action_button (event: Win32.MouseHookEvent) =
    if
        event.message = Win32Native.WM_MBUTTONDOWN
        || event.message = Win32Native.WM_MBUTTONUP
        || event.message = Win32Native.WM_MBUTTONDBLCLK
    then
        ValueSome Middle
    else
        side_button_from_data event.mouse_data

let action_button_down (button: SideButton) (message: int) =
    match button with
    | Middle -> message = Win32Native.WM_MBUTTONDOWN || message = Win32Native.WM_MBUTTONDBLCLK
    | Mouse4
    | Mouse5 -> message = Win32Native.WM_XBUTTONDOWN || message = Win32Native.WM_XBUTTONDBLCLK

let action_button_up (button: SideButton) (message: int) =
    match button with
    | Middle -> message = Win32Native.WM_MBUTTONUP
    | Mouse4
    | Mouse5 -> message = Win32Native.WM_XBUTTONUP

let raw_navigation_captures_button_messages () =
    RawNavigationCoordinator.captures_button_messages raw_navigation

let raw_navigation_button_message (message: int) =
    message = Win32Native.WM_RBUTTONDOWN
    || message = Win32Native.WM_RBUTTONUP
    || message = Win32Native.WM_RBUTTONDBLCLK
    || message = Win32Native.WM_MBUTTONDOWN
    || message = Win32Native.WM_MBUTTONUP
    || message = Win32Native.WM_MBUTTONDBLCLK
    || message = Win32Native.WM_XBUTTONDOWN
    || message = Win32Native.WM_XBUTTONUP
    || message = Win32Native.WM_XBUTTONDBLCLK

let handle_mouse_event (event: Win32.MouseHookEvent) =
    let mutable swallow = false
    let mutable rightClickEvent = false
    let mutable rightClickWasOwned = false

    try
        if state.lifecycle = Suspended then
            suspended_hook_events <- suspended_hook_events + 1L

            if
                event.message = Win32Native.WM_RBUTTONDOWN
                || event.message = Win32Native.WM_RBUTTONDBLCLK
            then
                suspended_hook_right_down <- suspended_hook_right_down + 1L
            elif event.message = Win32Native.WM_RBUTTONUP then
                suspended_hook_right_up <- suspended_hook_right_up + 1L
            elif raw_navigation_button_message event.message then
                suspended_hook_other_buttons <- suspended_hook_other_buttons + 1L

        if
            raw_navigation_button_message event.message
            && raw_navigation_captures_button_messages ()
        then
            true
        elif
            event.message = Win32Native.WM_RBUTTONDOWN
            || event.message = Win32Native.WM_RBUTTONUP
            || event.message = Win32Native.WM_RBUTTONDBLCLK
        then
            rightClickEvent <- true
            rightClickWasOwned <- RightClickTransitions.owns_button right_click

            swallow <-
                RightClickTransitions.handle_event
                    state
                    right_click
                    (ViewportRegistry.try_viewport viewport_registry)
                    (command_depth > 0)
                    event

            if RightClickTransitions.action_pending right_click then
                signal_hook_ui_work ()

            swallow
        else
            match action_button event with
            | ValueNone -> false
            | ValueSome button ->
                let hookActive = MouseHook.installed mouse_hook

                let isDown = action_button_down button event.message
                let isUp = action_button_up button event.message

                if
                    isDown
                    && MouseOverrideState.hook_button_ownership state button = ReleaseObserved
                then
                    // The watchdog saw the old Up outside Rhino, so this starts a new button pair.
                    MouseOverrideState.set_hook_button_ownership state button NotOwned

                let hookOwnsButton = MouseOverrideState.hook_owns_button state button

                if isUp && hookOwnsButton then
                    swallow <- true
                    MouseOverrideState.set_hook_button_ownership state button NotOwned

                    if state.lifecycle = Available then
                        state.pending_side_button_events.Enqueue(ButtonUp button)
                        signal_hook_ui_work ()

                    MouseOverrideState.keep_timer_running state
                    true
                elif isDown && hookOwnsButton then
                    swallow <- true
                    true
                elif
                    not hookActive
                    || state.lifecycle <> Available
                    || not (RoutedMouseAction.enabled (MouseOverrideState.action_for state button))
                then
                    false
                elif isDown && Win32Native.GetCapture() = nativeint 0 then
                    match ViewportRegistry.try_viewport viewport_registry event.hook_window with
                    | ValueSome hookViewport ->
                        match ViewportRegistry.try_viewport viewport_registry event.point_window with
                        | ValueSome pointViewport when
                            MouseOverrideState.same_host hookViewport.host pointViewport.host
                            && MouseOverrideState.capabilities_allowed state pointViewport.name
                            ->
                            swallow <- true
                            MouseOverrideState.set_hook_button_ownership state button Owned

                            state.pending_side_button_events.Enqueue(
                                ButtonDown(button, pointViewport.host, event.screen_point)
                            )

                            signal_hook_ui_work ()

                            MouseOverrideState.keep_timer_running state
                            true
                        | ValueSome _
                        | ValueNone -> false
                    | ValueNone -> false
                else
                    false
    with error ->
        log_exception "mouse override hook" error

        if rightClickEvent then
            rightClickWasOwned || RightClickTransitions.owns_button right_click
        else
            swallow

let mouse_hook_environment: MouseHook.Environment =
    { handle_event = handle_mouse_event
      subscribe_viewports = fun () -> ViewportRegistry.subscribe viewport_registry
      unsubscribe_viewports = fun () -> ViewportRegistry.unsubscribe viewport_registry
      viewports_subscribed = fun () -> ViewportRegistry.subscribed viewport_registry
      install_ui_wake =
        fun () ->
            match install_hook_ui_wake () with
            | Ok() -> ()
            | Error error -> Debug.WriteLine $"RhinosCanFly mouse-action UI wake: {error}"
      remove_ui_wake =
        fun () ->
            match remove_hook_ui_wake () with
            | Ok() -> ()
            | Error error -> Debug.WriteLine $"RhinosCanFly mouse-action UI wake cleanup: {error}"
      keep_watchdog_running = fun () -> MouseOverrideState.keep_watchdog_running state
      log_exception = log_exception }

let install_mouse_hook () =
    MouseHook.install mouse_hook mouse_hook_environment

let remove_mouse_hook () =
    MouseHook.remove mouse_hook mouse_hook_environment

let mouse_hook_needed () =
    match state.lifecycle with
    | ShutDown -> false
    | Suspended ->
        RightClickTransitions.owns_button right_click
        || MouseOverrideState.hook_owns_any_button state
    | Available
    | Resuming
    | Degraded _ ->
        RightClickTransitions.capture_needed state right_click
        || MouseOverrideState.side_button_routing_enabled state
        || MouseOverrideState.hook_owns_any_button state

let refresh_mouse_hook () =
    MouseHook.refresh mouse_hook mouse_hook_environment (mouse_hook_needed ())

let mouse_hook_needs_reconciliation () =
    MouseHook.needs_reconciliation mouse_hook mouse_hook_environment (mouse_hook_needed ())

let prune_released_side_buttons () =
    if MouseOverrideState.hook_owns_button state Middle then
        if SideButtonTransitions.is_down Middle then
            MouseOverrideState.set_hook_button_ownership state Middle Owned
        else
            MouseOverrideState.observe_hook_button_released state Middle

    if MouseOverrideState.hook_owns_button state Mouse4 then
        if SideButtonTransitions.is_down Mouse4 then
            MouseOverrideState.set_hook_button_ownership state Mouse4 Owned
        else
            MouseOverrideState.observe_hook_button_released state Mouse4

    if MouseOverrideState.hook_owns_button state Mouse5 then
        if SideButtonTransitions.is_down Mouse5 then
            MouseOverrideState.set_hook_button_ownership state Mouse5 Owned
        else
            MouseOverrideState.observe_hook_button_released state Mouse5

let reconcile_button_ownership_after_suspension () =
    RightClickTransitions.reconcile_physical_button right_click

    if MouseOverrideState.hook_owns_button state Middle then
        MouseOverrideState.set_hook_button_ownership
            state
            Middle
            (if SideButtonTransitions.is_down Middle then
                 Owned
             else
                 NotOwned)

    if MouseOverrideState.hook_owns_button state Mouse4 then
        MouseOverrideState.set_hook_button_ownership
            state
            Mouse4
            (if SideButtonTransitions.is_down Mouse4 then
                 Owned
             else
                 NotOwned)

    if MouseOverrideState.hook_owns_button state Mouse5 then
        MouseOverrideState.set_hook_button_ownership
            state
            Mouse5
            (if SideButtonTransitions.is_down Mouse5 then
                 Owned
             else
                 NotOwned)

let release_after_timer_error (error: exn) =
    log_exception "mouse override timer" error
    RightClickTransitions.clear_action right_click

    match RawNavigationCoordinator.release raw_navigation with
    | Ok() -> ()
    | Error cleanupError -> Debug.WriteLine $"RhinosCanFly mouse override timer cleanup: {cleanupError}"

let hook_removal_pending () = MouseHook.removal_pending mouse_hook

let hook_removal_abandoned () = MouseHook.removal_abandoned mouse_hook

let poll_requirement () =
    if
        RightClickTransitions.action_pending right_click
        || MouseOverrideState.fast_poll_required state
    then
        PollFast
    elif
        (match state.lifecycle with
         | Degraded _ -> not (hook_removal_abandoned ())
         | Available
         | Suspended
         | Resuming
         | ShutDown -> false)
        || MouseOverrideState.hook_owns_any_button state
        || RightClickTransitions.owns_button right_click
        || RawNavigationCoordinator.is_present raw_navigation
        || hook_removal_pending ()
        || mouse_hook_needs_reconciliation ()
    then
        PollWatchdog
    else
        PollStopped

let apply_poll_requirement () =
    match poll_requirement () with
    | PollFast -> MouseOverrideState.keep_timer_running state
    | PollWatchdog -> MouseOverrideState.keep_watchdog_running state
    | PollStopped ->
        if state.poll_timer.Enabled then
            state.poll_timer.Stop()

let activate_degraded (error: string) =
    state.lifecycle <- Degraded error

    match RawNavigationCoordinator.stop raw_navigation with
    | Ok() -> ()
    | Error cleanupError -> Debug.WriteLine $"RhinosCanFly raw navigation cleanup: {cleanupError}"

    try
        apply_poll_requirement ()
    with timerError ->
        log_exception "mouse override recovery timer" timerError

let activate_available () =
    try
        state.lifecycle <- Available

        match RawNavigationCoordinator.reconcile raw_navigation with
        | Error error ->
            let message = $"Could not activate mouse button overrides: {error}"
            activate_degraded message
            Error message
        | Ok() ->
            apply_poll_requirement ()
            Ok()
    with error ->
        let message = $"Could not activate mouse button overrides: {error.Message}"
        activate_degraded message
        Error message

let poll_timer_elapsed () =
    try
        let navigationWasActive =
            MouseOverrideState.gesture_navigation_engaged state
            || MouseOverrideState.view_latch_engaged state
            || ValueOption.isSome (RightClickTransitions.direct_navigation_host right_click)

        let mutable navigationCleanupFailed = false

        try
            SideButtonTransitions.process_hook_events state
            prune_released_side_buttons ()
            RightClickTransitions.prune_released_button right_click
            RightClickTransitions.update state right_click (command_depth > 0)

            let foreground = MouseOverrideState.foreground_root_window ()

            let navigationLostFocus =
                match RawNavigationCoordinator.active_host raw_navigation with
                | ValueSome expected -> foreground <> expected.root_window
                | ValueNone -> false

            if navigationLostFocus then
                match RawNavigationCoordinator.release raw_navigation with
                | Ok() -> ()
                | Error error ->
                    navigationCleanupFailed <- true
                    Debug.WriteLine $"RhinosCanFly mouse override focus loss: {error}"
            elif state.navigation_exit_requested || MouseOverrideState.exit_key_down state then
                match RawNavigationCoordinator.release raw_navigation with
                | Ok() -> ()
                | Error error ->
                    navigationCleanupFailed <- true
                    Debug.WriteLine $"RhinosCanFly mouse override exit: {error}"
            else
                GestureNavigationTransitions.poll state

                ViewLatchTransitions.update state

            if not navigationCleanupFailed then
                match RawNavigationCoordinator.reconcile raw_navigation with
                | Ok() -> ()
                | Error error -> failwith error

            if
                navigationWasActive
                && not (MouseOverrideState.gesture_navigation_engaged state)
                && not (MouseOverrideState.view_latch_engaged state)
                && ValueOption.isNone (RightClickTransitions.direct_navigation_host right_click)
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
            let mouseResult = refresh_mouse_hook ()

            match mouseResult with
            | Ok() -> ()
            | Error error -> Debug.WriteLine $"RhinosCanFly mouse override hook: {error}"

            // Keep these matches nested because reference tuples allocate.
            match state.lifecycle with
            | Degraded _ when state.suspension_ids.Count = 0 ->
                match mouseResult with
                | Ok() ->
                    match activate_available () with
                    | Ok() -> ()
                    | Error error -> Debug.WriteLine $"RhinosCanFly mouse override activation: {error}"
                | Error _ -> apply_poll_requirement ()
            | Available
            | Suspended
            | Resuming
            | Degraded _
            | ShutDown -> apply_poll_requirement ()
        else
            apply_poll_requirement ()
    with error ->
        release_after_timer_error error

        try
            apply_poll_requirement ()
        with stopError ->
            Debug.WriteLine $"RhinosCanFly mouse override timer scheduling: {stopError}"

do hook_ui_work_requested.Publish.Add(fun () -> poll_timer_elapsed ())

state.poll_timer.Tick.Add(fun (_: EventArgs) -> poll_timer_elapsed ())

let keeps_navigation_active (commandName: string) =
    String.Equals(commandName, "RhinosCanFlyPivot", StringComparison.Ordinal)
    || String.Equals(commandName, "RhinosCanFlyPan", StringComparison.Ordinal)

let command_began =
    EventHandler<CommandEventArgs>(fun (_: obj) (event: CommandEventArgs) ->
        InputDebugTrace.write
            $"mouse overrides command begin name={event.CommandEnglishName} depth-before={command_depth} lifecycle={state.lifecycle} hook-installed={MouseHook.installed mouse_hook} capture={Win32Native.GetCapture()}"

        command_depth <- command_depth + 1

        try
            RightClickTransitions.command_began right_click

            if
                not (keeps_navigation_active event.CommandEnglishName)
                && state.lifecycle = Available
                && (state.pending_side_button_events.Count > 0
                    || MouseOverrideState.gesture_navigation_engaged state
                    || MouseOverrideState.view_latch_engaged state
                    || RawNavigationCoordinator.is_present raw_navigation)
            then
                match RawNavigationCoordinator.release raw_navigation with
                | Ok() -> ()
                | Error error -> Debug.WriteLine $"RhinosCanFly command navigation cleanup: {error}"

                apply_poll_requirement ()
                request_ui_redraw ()
        with error ->
            log_exception "command navigation callback" error)

let command_ended =
    EventHandler<CommandEventArgs>(fun (_: obj) (event: CommandEventArgs) ->
        InputDebugTrace.write
            $"mouse overrides command end name={event.CommandEnglishName} depth-before={command_depth} lifecycle={state.lifecycle} hook-installed={MouseHook.installed mouse_hook} capture={Win32Native.GetCapture()}"

        if command_depth > 0 then
            command_depth <- command_depth - 1

        if MouseHook.installed mouse_hook then
            ViewportRegistry.refresh_active viewport_registry)

do Command.BeginCommand.AddHandler command_began
do Command.EndCommand.AddHandler command_ended

let start_view_latch (view: RhinoView) (mode: ViewNavigationMode) (completion: Action option) =
    if isNull view || isNull view.Document || view.Handle = nativeint 0 then
        Error "The active viewport is unavailable."
    elif not (MouseOverrideState.capabilities_allowed state view.ActiveViewport.Name) then
        Error "RhinosCanFly capabilities are disabled for this viewport."
    else
        let host = ViewportRegistry.capture_host view

        let replacementResult =
            match ViewLatchTransitions.current_mode state with
            | Some current ->
                if current = mode then
                    Ok()
                else
                    RawNavigationCoordinator.release raw_navigation
            | None ->
                if MouseOverrideState.gesture_navigation_engaged state then
                    RawNavigationCoordinator.release raw_navigation
                else
                    Ok()

        match replacementResult with
        | Error error -> Error error
        | Ok() ->
            let originalTarget = view.ActiveViewport.CameraTarget

            match ViewLatchTransitions.start_or_switch state host mode completion with
            | Error error -> Error error
            | Ok() ->
                let activation =
                    match RawNavigationCoordinator.reconcile raw_navigation with
                    | Error error -> Error error
                    | Ok() -> refresh_mouse_hook ()

                match activation with
                | Ok() -> Ok()
                | Error activationError ->
                    let mutable error = activationError

                    match ViewLatchTransitions.release state with
                    | Ok() -> ()
                    | Error cleanupError -> error <- $"{error}; cleanup failed: {cleanupError}"

                    match RawNavigationCoordinator.reconcile raw_navigation with
                    | Ok() -> ()
                    | Error cleanupError -> error <- $"{error}; raw cleanup failed: {cleanupError}"

                    try
                        if ViewportRegistry.view_matches_host host view then
                            view.ActiveViewport.SetCameraTarget(originalTarget, false)
                    with targetError ->
                        error <- $"{error}; target rollback failed: {targetError.Message}"

                    Error error

let stop_view_latch (mode: ViewNavigationMode) =
    let wasActive = ViewLatchTransitions.is_mode state mode

    let rawStopResult =
        if wasActive then
            RawNavigationCoordinator.stop raw_navigation
        else
            Ok()

    let navigationResult = ViewLatchTransitions.stop state mode
    let rawReconcileResult = RawNavigationCoordinator.reconcile raw_navigation
    let errors = ResizeArray<string>()

    match rawStopResult with
    | Ok() -> ()
    | Error error -> errors.Add $"raw cleanup failed: {error}"

    match navigationResult with
    | Ok() -> ()
    | Error error -> errors.Add error

    match rawReconcileResult with
    | Ok() -> ()
    | Error error -> errors.Add $"raw reconciliation failed: {error}"

    let result =
        if errors.Count = 0 then
            Ok()
        else
            Error(String.concat "; " errors)

    match refresh_mouse_hook () with
    | Ok() -> ()
    | Error error -> Debug.WriteLine $"RhinosCanFly mouse override hook: {error}"

    apply_poll_requirement ()

    if wasActive && not (MouseOverrideState.view_latch_engaged state) then
        request_ui_redraw ()

    result

let view_latch_is (mode: ViewNavigationMode) = ViewLatchTransitions.is_mode state mode

let apply (config: MouseOverrideConfig) =
    InputDebugTrace.write
        $"PlatformMouseActions.apply begin lifecycle={state.lifecycle} suspensions={state.suspension_ids.Count} hook-installed={MouseHook.installed mouse_hook} capture={Win32Native.GetCapture()}"

    if state.lifecycle = ShutDown then
        InputDebugTrace.write "PlatformMouseActions.apply end result=shutdown-error"
        Error "Mouse button overrides have already shut down."
    else
        RightClickTransitions.clear_action right_click

        match RawNavigationCoordinator.release raw_navigation with
        | Error error ->
            InputDebugTrace.write $"PlatformMouseActions.apply navigation release result=error error={error}"
            activate_degraded error
            Error error
        | Ok() ->
            state.routing <- config

            if state.lifecycle = Suspended then
                InputDebugTrace.write "PlatformMouseActions.apply end result=ok suspended=true"
                Ok()
            else
                state.lifecycle <- Resuming

                try
                    match refresh_mouse_hook () with
                    | Error error ->
                        InputDebugTrace.write $"PlatformMouseActions.apply hook refresh result=error error={error}"
                        activate_degraded error
                        Error error
                    | Ok() ->
                        let result = activate_available ()
                        InputDebugTrace.write $"PlatformMouseActions.apply end result={result}"
                        result
                with error ->
                    let message = $"Could not apply mouse button overrides: {error.Message}"
                    log_exception "mouse override configuration" error
                    activate_degraded message
                    InputDebugTrace.write $"PlatformMouseActions.apply exception={error}"
                    Error message

let suspend () =
    InputDebugTrace.write
        $"PlatformMouseActions.suspend begin lifecycle={state.lifecycle} suspensions={state.suspension_ids.Count} hook-installed={MouseHook.installed mouse_hook} hook-pending={hook_removal_pending ()} right-owned={RightClickTransitions.owns_button right_click} side-owned={MouseOverrideState.hook_owns_any_button state} capture={Win32Native.GetCapture()}"

    if state.lifecycle = ShutDown then
        InputDebugTrace.write "PlatformMouseActions.suspend end result=shutdown-error"
        Error "Mouse button overrides have already shut down."
    elif state.suspension_ids.Count > 0 then
        state.next_suspension_id <- state.next_suspension_id + 1L

        let lease =
            { id = state.next_suspension_id
              cleanup_error = state.suspension_cleanup_error }

        state.suspension_ids.Add lease.id |> ignore

        InputDebugTrace.write
            $"PlatformMouseActions.suspend end result=ok nested=true lease={lease.id} suspensions={state.suspension_ids.Count} cleanup-error={lease.cleanup_error}"

        Ok lease
    else
        let errors = ResizeArray<string>()
        suspended_hook_events <- 0L
        suspended_hook_right_down <- 0L
        suspended_hook_right_up <- 0L
        suspended_hook_other_buttons <- 0L
        state.lifecycle <- Suspended
        RightClickTransitions.clear_action right_click

        InputDebugTrace.write
            $"PlatformMouseActions.suspend lifecycle set hook-needed={mouse_hook_needed ()} right-owned={RightClickTransitions.owns_button right_click} side-owned={MouseOverrideState.hook_owns_any_button state}"

        try
            InputDebugTrace.write "PlatformMouseActions.suspend raw navigation release begin"

            match RawNavigationCoordinator.release raw_navigation with
            | Ok() -> InputDebugTrace.write "PlatformMouseActions.suspend raw navigation release end result=ok"
            | Error error ->
                InputDebugTrace.write
                    $"PlatformMouseActions.suspend raw navigation release end result=error error={error}"

                errors.Add error
        with error ->
            InputDebugTrace.write $"PlatformMouseActions.suspend raw navigation release exception={error}"
            log_exception "mouse override suspension cleanup" error
            errors.Add error.Message

        try
            InputDebugTrace.write
                $"PlatformMouseActions.suspend hook refresh begin installed={MouseHook.installed mouse_hook} needed={mouse_hook_needed ()} capture={Win32Native.GetCapture()}"

            match refresh_mouse_hook () with
            | Ok() ->
                InputDebugTrace.write
                    $"PlatformMouseActions.suspend hook refresh end result=ok installed={MouseHook.installed mouse_hook} pending={hook_removal_pending ()}"
            | Error error ->
                InputDebugTrace.write
                    $"PlatformMouseActions.suspend hook refresh end result=error installed={MouseHook.installed mouse_hook} pending={hook_removal_pending ()} error={error}"

                errors.Add error
        with error ->
            InputDebugTrace.write $"PlatformMouseActions.suspend hook refresh exception={error}"
            log_exception "mouse override suspension hook" error
            errors.Add error.Message

        try
            InputDebugTrace.write "PlatformMouseActions.suspend poll apply begin"
            apply_poll_requirement ()

            InputDebugTrace.write
                $"PlatformMouseActions.suspend poll apply end timer-enabled={state.poll_timer.Enabled}"
        with error ->
            InputDebugTrace.write $"PlatformMouseActions.suspend poll apply exception={error}"
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

        InputDebugTrace.write
            $"PlatformMouseActions.suspend end result=ok lease={lease.id} suspensions={state.suspension_ids.Count} cleanup-error={cleanupError} hook-installed={MouseHook.installed mouse_hook} hook-pending={hook_removal_pending ()} capture={Win32Native.GetCapture()}"

        Ok lease

let resume (lease: InputSuspensionLease) =
    InputDebugTrace.write
        $"PlatformMouseActions.resume begin lease={lease.id} lifecycle={state.lifecycle} suspensions={state.suspension_ids.Count} hook-installed={MouseHook.installed mouse_hook} hook-pending={hook_removal_pending ()} capture={Win32Native.GetCapture()} suspended-events={suspended_hook_events} right-down={suspended_hook_right_down} right-up={suspended_hook_right_up} other-buttons={suspended_hook_other_buttons}"

    if state.lifecycle = ShutDown then
        InputDebugTrace.write $"PlatformMouseActions.resume end lease={lease.id} result=shutdown-error"
        Error "Mouse button overrides have already shut down."
    elif not (state.suspension_ids.Remove lease.id) then
        InputDebugTrace.write $"PlatformMouseActions.resume end lease={lease.id} result=ok unknown-lease=true"
        Ok()
    elif state.suspension_ids.Count > 0 then
        InputDebugTrace.write
            $"PlatformMouseActions.resume end lease={lease.id} result=ok remaining-suspensions={state.suspension_ids.Count}"

        Ok()
    else
        state.suspension_cleanup_error <- None
        state.lifecycle <- Resuming

        try
            InputDebugTrace.write "PlatformMouseActions.resume ownership reconciliation begin"
            reconcile_button_ownership_after_suspension ()

            InputDebugTrace.write
                $"PlatformMouseActions.resume ownership reconciliation end right-owned={RightClickTransitions.owns_button right_click} side-owned={MouseOverrideState.hook_owns_any_button state}"

            InputDebugTrace.write
                $"PlatformMouseActions.resume hook refresh begin installed={MouseHook.installed mouse_hook} needed={mouse_hook_needed ()} capture={Win32Native.GetCapture()}"

            match refresh_mouse_hook () with
            | Error error ->
                InputDebugTrace.write
                    $"PlatformMouseActions.resume hook refresh end result=error installed={MouseHook.installed mouse_hook} pending={hook_removal_pending ()} error={error}"

                activate_degraded error
                Error error
            | Ok() ->
                InputDebugTrace.write
                    $"PlatformMouseActions.resume hook refresh end result=ok installed={MouseHook.installed mouse_hook} pending={hook_removal_pending ()}"

                ViewportRegistry.refresh_active viewport_registry
                let result = activate_available ()

                InputDebugTrace.write
                    $"PlatformMouseActions.resume end lease={lease.id} result={result} lifecycle={state.lifecycle} hook-installed={MouseHook.installed mouse_hook} capture={Win32Native.GetCapture()}"

                result
        with error ->
            let message = $"Could not resume mouse button overrides: {error.Message}"
            log_exception "mouse override resume" error
            activate_degraded message
            InputDebugTrace.write $"PlatformMouseActions.resume exception lease={lease.id} error={error}"
            Error message

let retry_hook_cleanup () =
    let errors = ResizeArray<string>()

    let attempt (name: string) (action: unit -> unit) =
        try
            action ()
        with error ->
            log_exception $"mouse override recovery {name}" error
            errors.Add $"{name}: {error.Message}"

    match RawNavigationCoordinator.release raw_navigation with
    | Ok() -> ()
    | Error error -> errors.Add $"view navigation: {error}"

    if MouseHook.removal_failed mouse_hook then
        match remove_mouse_hook () with
        | Ok() -> ()
        | Error error -> errors.Add $"mouse hook: {error}"

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
        RightClickTransitions.reset right_click
        state.suspension_ids.Clear()
        state.suspension_cleanup_error <- None

        attempt "command handler" (fun () -> Command.BeginCommand.RemoveHandler command_began)
        attempt "command end handler" (fun () -> Command.EndCommand.RemoveHandler command_ended)

        attempt "application initialized handler" (fun () ->
            ViewportRegistry.remove_application_handler viewport_registry)

        attempt "view navigation" (fun () ->
            match RawNavigationCoordinator.release raw_navigation with
            | Ok() -> ()
            | Error error -> Debug.WriteLine $"RhinosCanFly mouse override shutdown: {error}")

        attempt "side-button ownership" (fun () ->
            MouseOverrideState.set_hook_button_ownership state Middle NotOwned
            MouseOverrideState.set_hook_button_ownership state Mouse4 NotOwned
            MouseOverrideState.set_hook_button_ownership state Mouse5 NotOwned)

        attempt "mouse hook" (fun () ->
            match remove_mouse_hook () with
            | Ok() -> ()
            | Error error -> failwith error)

        if MouseHook.absent mouse_hook then
            attempt "mouse-action UI wake" (fun () ->
                match remove_hook_ui_wake () with
                | Ok() -> ()
                | Error error -> failwith error)

        attempt "timer" (fun () -> state.poll_timer.Dispose())
