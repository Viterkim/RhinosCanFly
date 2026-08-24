module RhinosCanFly.Platform.Win.MouseButtonOverrides

// Rhino 9 deprecates GetViewList(bool, bool), but Rhino 7 has no replacement.
#nowarn "44"

open System
open System.Diagnostics
open System.Drawing
open Rhino
open Rhino.ApplicationSettings
open Rhino.Commands
open Rhino.Display
open RhinosCanFly
open RhinosCanFly.Platform.Win.ViewNavigationTypes

let state = create_state ()
let right_click = RightClickTransitions.create ()
let mutable raw_navigation: RawViewNavigation.Session option = None

let request_navigation_exit () =
    state.navigation_exit_requested <- true
    ViewNavigationState.keep_timer_running state

type HookState =
    | HookAbsent
    | HookInstalled of Win32Native.WindowsHook
    | HookRemovalPending of hook: Win32Native.WindowsHook * error: string * attempts: int
    | HookRemovalAbandoned of hook: Win32Native.WindowsHook * error: string

[<Literal>]
let MAXIMUM_HOOK_REMOVAL_ATTEMPTS = 8

let mutable mouse_hook_state = HookAbsent
let mutable command_depth = if Command.InCommand() then 1 else 0

let log_exception (context: string) (error: exn) =
    Debug.WriteLine $"RhinosCanFly {context}: {error}"

let request_ui_redraw () =
    try
        Win32.request_application_redraw (RhinoApp.MainWindowHandle())
    with error ->
        log_exception "UI redraw request" error

let view_matches_host (host: ViewportHostIdentity) (view: RhinoView) =
    let (ViewWindowHandle expectedWindow) = host.view_window
    let document = view.Document

    not (Object.ReferenceEquals(document, null))
    && view.RuntimeSerialNumber = host.view_serial_number
    && document.RuntimeSerialNumber = host.document_serial_number
    && view.Handle = expectedWindow
    && view.ActiveViewportID = host.viewport_id
    && ViewNavigationState.root_window view.Handle = host.root_window

let active_navigation_host () =
    match RightClickTransitions.direct_navigation_host right_click with
    | ValueSome host -> ValueSome host
    | ValueNone -> ViewNavigationState.navigation_host state

[<Struct>]
type DesiredRawNavigation =
    { host: ViewportHostIdentity
      mode: RawViewNavigation.Mode }

let desired_raw_navigation () =
    if state.lifecycle <> Available then
        ValueNone
    else
        match RightClickTransitions.parallel_zoom_host right_click with
        | ValueSome host ->
            ValueSome
                { host = host
                  mode = RawViewNavigation.Mode.ParallelZoom }
        | ValueNone ->
            match RightClickTransitions.parallel_pan_host right_click with
            | ValueSome host ->
                ValueSome
                    { host = host
                      mode = RawViewNavigation.Mode.Pan }
            | ValueNone when ViewNavigationState.gesture_navigation_engaged state ->
                match ViewNavigationState.gesture_navigation_host state with
                | ValueSome host ->
                    match ViewNavigationState.gesture_navigation_mode state with
                    | ValueSome ViewNavigationMode.Pivot ->
                        ValueSome
                            { host = host
                              mode = RawViewNavigation.Mode.Pivot }
                    | ValueSome ViewNavigationMode.Pan ->
                        ValueSome
                            { host = host
                              mode = RawViewNavigation.Mode.Pan }
                    | ValueNone -> ValueNone
                | ValueNone -> ValueNone
            | ValueNone ->
                match ViewNavigationState.navigation_host state with
                | ValueSome host ->
                    match state.view_latch with
                    | PivotActive _ ->
                        ValueSome
                            { host = host
                              mode = RawViewNavigation.Mode.Pivot }
                    | PanActive _ ->
                        ValueSome
                            { host = host
                              mode = RawViewNavigation.Mode.Pan }
                    | NoViewLatch
                    | WaitingForRelease _ -> ValueNone
                | ValueNone -> ValueNone

let stop_raw_navigation () =
    match raw_navigation with
    | None -> Ok()
    | Some session ->
        let result = session.Stop()
        raw_navigation <- None
        result

let raw_button_event (events: RawMouseButtonEvents) (requested: RawMouseButtonEvents) =
    events &&& requested <> RawMouseButtonEvents.None

let handle_raw_right_down (host: ViewportHostIdentity) (screenPoint: Point) =
    right_click.button_ownership <- Owned

    match RightClickTransitions.requested_gesture_action state (RightClickTransitions.modifiers ()) with
    | ValueSome action ->
        GestureNavigationTransitions.press_or_log state GestureOwner.ModifiedRightClick action host screenPoint
    | ValueNone when state.routing.exit_on_mouse_right -> request_navigation_exit ()
    | ValueNone -> ()

let handle_raw_right_up () =
    right_click.button_ownership <- NotOwned
    RightClickTransitions.clear_action right_click
    GestureNavigationTransitions.release state GestureOwner.ModifiedRightClick

let handle_raw_side_down (button: SideButton) (host: ViewportHostIdentity) (screenPoint: Point) =
    ViewNavigationState.set_hook_button_ownership state button Owned

    GestureNavigationTransitions.press_or_log
        state
        (SideButtonTransitions.owner button)
        (ViewNavigationState.action_for state button)
        host
        screenPoint

let handle_raw_side_up (button: SideButton) =
    ViewNavigationState.set_hook_button_ownership state button NotOwned
    GestureNavigationTransitions.release state (SideButtonTransitions.owner button)

let handle_raw_navigation_buttons (host: ViewportHostIdentity) (events: RawMouseButtonEvents) (screenPoint: Point) =
    try
        if
            raw_button_event events RawMouseButtonEvents.LeftUp
            && state.routing.exit_on_mouse_left
        then
            request_navigation_exit ()

        let rightDown = raw_button_event events RawMouseButtonEvents.RightDown
        let rightUp = raw_button_event events RawMouseButtonEvents.RightUp

        if rightDown && rightUp && Win32Native.GetAsyncKeyState Win32Native.VK_RBUTTON < 0s then
            handle_raw_right_up ()
            handle_raw_right_down host screenPoint
        else
            if rightDown then
                handle_raw_right_down host screenPoint

            if rightUp then
                handle_raw_right_up ()

        let mouse4Down = raw_button_event events RawMouseButtonEvents.Mouse4Down
        let mouse4Up = raw_button_event events RawMouseButtonEvents.Mouse4Up

        if mouse4Down && mouse4Up && SideButtonTransitions.is_down Mouse4 then
            handle_raw_side_up Mouse4
            handle_raw_side_down Mouse4 host screenPoint
        else
            if mouse4Down then
                handle_raw_side_down Mouse4 host screenPoint

            if mouse4Up then
                handle_raw_side_up Mouse4

        let mouse5Down = raw_button_event events RawMouseButtonEvents.Mouse5Down
        let mouse5Up = raw_button_event events RawMouseButtonEvents.Mouse5Up

        if mouse5Down && mouse5Up && SideButtonTransitions.is_down Mouse5 then
            handle_raw_side_up Mouse5
            handle_raw_side_down Mouse5 host screenPoint
        else
            if mouse5Down then
                handle_raw_side_down Mouse5 host screenPoint

            if mouse5Up then
                handle_raw_side_up Mouse5

        ViewNavigationState.keep_timer_running state
    with error ->
        log_exception "raw navigation buttons" error
        request_navigation_exit ()

let start_raw_navigation (desired: DesiredRawNavigation) =
    let failed = Action request_navigation_exit

    let buttonEvents =
        Action<RawMouseButtonEvents, Point>(fun (events: RawMouseButtonEvents) (point: Point) ->
            handle_raw_navigation_buttons desired.host events point)

    match RawViewNavigation.start desired.host desired.mode buttonEvents failed with
    | Error error ->
        match GestureNavigationTransitions.rollback_start state with
        | Ok() -> Error error
        | Error rollbackError -> Error $"{error}; {rollbackError}"
    | Ok session ->
        raw_navigation <- Some session
        Ok()

let reconcile_raw_navigation () =
    match desired_raw_navigation () with
    | ValueNone -> stop_raw_navigation ()
    | ValueSome desired ->
        match raw_navigation with
        | Some current when current.Matches(desired.host, desired.mode) -> Ok()
        | Some _ ->
            match stop_raw_navigation () with
            | Error error ->
                match GestureNavigationTransitions.rollback_start state with
                | Ok() -> Error error
                | Error rollbackError -> Error $"{error}; {rollbackError}"
            | Ok() -> start_raw_navigation desired
        | None -> start_raw_navigation desired

let release_navigation () =
    RightClickTransitions.clear_direct_navigation right_click
    let viewResult = ViewNavigationState.release_all state
    let rawResult = stop_raw_navigation ()

    match viewResult with
    | Error error ->
        match rawResult with
        | Error rawError -> Error $"{error}; raw navigation: {rawError}"
        | Ok() -> Error error
    | Ok() -> rawResult

let mutable right_click_viewports: RightClickTransitions.RightClickViewport array =
    Array.empty

let mutable view_create_subscribed = false
let mutable view_destroy_subscribed = false

let capture_viewport_host (view: RhinoView) =
    { view_serial_number = view.RuntimeSerialNumber
      document_serial_number = view.Document.RuntimeSerialNumber
      viewport_id = view.ActiveViewportID
      view_window = ViewWindowHandle view.Handle
      root_window = ViewNavigationState.root_window view.Handle }

let capture_right_click_viewport (view: RhinoView) : RightClickTransitions.RightClickViewport =
    { host = capture_viewport_host view
      name = view.ActiveViewport.Name
      is_perspective = view.ActiveViewport.IsPerspectiveProjection
      is_parallel = view.ActiveViewport.IsParallelProjection }

let update_right_click_viewport (view: RhinoView) =
    let updated = capture_right_click_viewport view
    let mutable index = 0
    let mutable found = false

    while index < right_click_viewports.Length && not found do
        if right_click_viewports[index].host.view_serial_number = updated.host.view_serial_number then
            right_click_viewports[index] <- updated
            found <- true

        index <- index + 1

    if not found then
        right_click_viewports <- Array.append right_click_viewports [| updated |]

let refresh_right_click_viewports () =
    try
        let refreshed =
            RhinoDoc.OpenDocuments()
            |> Array.collect (fun (document: RhinoDoc) -> document.Views.GetViewList(true, true))
            |> Array.choose (fun (view: RhinoView) ->
                if isNull view || isNull view.Document || view.Handle = nativeint 0 then
                    None
                else
                    Some(capture_right_click_viewport view))

        right_click_viewports <- refreshed
        Ok()
    with error ->
        log_exception "viewport window refresh" error
        Error $"Could not enumerate Rhino viewport windows: {error.Message}"

let application_initialized =
    EventHandler(fun (_: obj) (_: EventArgs) ->
        match mouse_hook_state with
        | HookInstalled _ ->
            match refresh_right_click_viewports () with
            | Ok() -> ()
            | Error error -> Debug.WriteLine $"RhinosCanFly initialized viewport refresh: {error}"
        | HookAbsent
        | HookRemovalPending _
        | HookRemovalAbandoned _ -> ())

do RhinoApp.Initialized.AddHandler application_initialized

let view_created =
    EventHandler<ViewEventArgs>(fun (_: obj) (event: ViewEventArgs) ->
        try
            let view = event.View

            if not (isNull view) && not (isNull view.Document) && view.Handle <> nativeint 0 then
                update_right_click_viewport view
        with error ->
            log_exception "viewport window created" error)

let view_destroyed =
    EventHandler<ViewEventArgs>(fun (_: obj) (event: ViewEventArgs) ->
        try
            let view = event.View

            if not (isNull view) then
                let serialNumber = view.RuntimeSerialNumber

                match active_navigation_host () with
                | ValueSome host when host.view_serial_number = serialNumber -> request_navigation_exit ()
                | ValueSome _
                | ValueNone -> ()

                right_click_viewports <-
                    right_click_viewports
                    |> Array.filter (fun (candidate: RightClickTransitions.RightClickViewport) ->
                        candidate.host.view_serial_number <> serialNumber)
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

        match refresh_right_click_viewports () with
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

        right_click_viewports <- Array.empty
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

        right_click_viewports <- Array.empty

        if errors.Count > 0 then
            failwith (String.concat "; " errors)

let try_right_click_viewport (window: nativeint) =
    let mutable index = 0
    let mutable result = ValueNone

    while index < right_click_viewports.Length && ValueOption.isNone result do
        let candidate = right_click_viewports[index]
        let (ViewWindowHandle candidateWindow) = candidate.host.view_window

        if
            Win32Native.IsWindow candidateWindow
            && Win32Native.IsWindowEnabled candidateWindow
            && (candidateWindow = window || Win32Native.IsChild(candidateWindow, window))
        then
            result <- ValueSome candidate

        index <- index + 1

    result

let side_button_from_data (mouseData: uint32) =
    match mouseData >>> 16 with
    | Win32Native.XBUTTON1 -> ValueSome Mouse4
    | Win32Native.XBUTTON2 -> ValueSome Mouse5
    | _ -> ValueNone

let raw_navigation_active () =
    match raw_navigation with
    | Some session -> session.IsActive
    | None -> false

let raw_navigation_button_message (message: int) =
    message = Win32Native.WM_RBUTTONDOWN
    || message = Win32Native.WM_RBUTTONUP
    || message = Win32Native.WM_RBUTTONDBLCLK
    || message = Win32Native.WM_XBUTTONDOWN
    || message = Win32Native.WM_XBUTTONUP
    || message = Win32Native.WM_XBUTTONDBLCLK

let handle_mouse_event (event: Win32.MouseHookEvent) =
    let mutable swallow = false
    let mutable rightClickEvent = false
    let mutable rightClickWasOwned = false

    try
        if raw_navigation_active () && raw_navigation_button_message event.message then
            true
        elif
            event.message = Win32Native.WM_RBUTTONDOWN
            || event.message = Win32Native.WM_RBUTTONUP
            || event.message = Win32Native.WM_RBUTTONDBLCLK
        then
            rightClickEvent <- true
            rightClickWasOwned <- RightClickTransitions.owns_button right_click

            swallow <-
                RightClickTransitions.handle_event state right_click try_right_click_viewport (command_depth > 0) event

            swallow
        else
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
                    ViewNavigationState.set_hook_button_ownership state button NotOwned

                let hookOwnsButton = ViewNavigationState.hook_owns_button state button

                if isUp && hookOwnsButton then
                    swallow <- true
                    ViewNavigationState.set_hook_button_ownership state button NotOwned

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
                    || not (RoutedMouseAction.enabled (ViewNavigationState.action_for state button))
                then
                    false
                elif isDown then
                    match try_right_click_viewport event.hook_window with
                    | ValueSome hookViewport ->
                        match try_right_click_viewport event.point_window with
                        | ValueSome pointViewport when
                            ViewNavigationState.same_host hookViewport.host pointViewport.host
                            && pointViewport.host.root_window = ViewNavigationState.foreground_root_window ()
                            ->
                            swallow <- true
                            ViewNavigationState.set_hook_button_ownership state button Owned

                            state.pending_side_button_events.Enqueue(
                                ButtonDown(button, pointViewport.host, event.screen_point)
                            )

                            ViewNavigationState.keep_timer_running state
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
                | HookRemovalAbandoned _ -> MAXIMUM_HOOK_REMOVAL_ATTEMPTS
                | HookAbsent
                | HookInstalled _ -> 1

            if nextAttempt >= MAXIMUM_HOOK_REMOVAL_ATTEMPTS then
                mouse_hook_state <- HookRemovalAbandoned(hook, error)
            else
                mouse_hook_state <- HookRemovalPending(hook, error, nextAttempt)
                ViewNavigationState.keep_watchdog_running state

            Error error

let mouse_hook_needed () =
    state.lifecycle <> ShutDown
    && (RightClickTransitions.capture_needed state right_click
        || ViewNavigationState.side_button_routing_enabled state
        || ViewNavigationState.hook_owns_any_button state)

let refresh_mouse_hook () =
    if mouse_hook_needed () then
        install_mouse_hook ()
    else
        remove_mouse_hook ()

let mouse_hook_needs_reconciliation () =
    match mouse_hook_state with
    | HookAbsent -> mouse_hook_needed () || view_create_subscribed || view_destroy_subscribed
    | HookInstalled _ -> not (mouse_hook_needed ())
    | HookRemovalPending _ -> true
    | HookRemovalAbandoned _ -> false

let prune_released_side_buttons () =
    if ViewNavigationState.hook_owns_button state Mouse4 then
        if SideButtonTransitions.is_down Mouse4 then
            ViewNavigationState.set_hook_button_ownership state Mouse4 Owned
        else
            ViewNavigationState.observe_hook_button_released state Mouse4

    if ViewNavigationState.hook_owns_button state Mouse5 then
        if SideButtonTransitions.is_down Mouse5 then
            ViewNavigationState.set_hook_button_ownership state Mouse5 Owned
        else
            ViewNavigationState.observe_hook_button_released state Mouse5

let release_after_timer_error (error: exn) =
    log_exception "mouse override timer" error
    RightClickTransitions.clear_action right_click

    match release_navigation () with
    | Ok() -> ()
    | Error cleanupError -> Debug.WriteLine $"RhinosCanFly mouse override timer cleanup: {cleanupError}"

let hook_removal_pending () =
    match mouse_hook_state with
    | HookRemovalPending _ -> true
    | HookAbsent
    | HookInstalled _
    | HookRemovalAbandoned _ -> false

let hook_removal_abandoned () =
    match mouse_hook_state with
    | HookRemovalAbandoned _ -> true
    | HookAbsent
    | HookInstalled _
    | HookRemovalPending _ -> false

let poll_requirement () =
    if
        RightClickTransitions.action_pending right_click
        || ViewNavigationState.fast_poll_required state
    then
        PollFast
    elif
        (match state.lifecycle with
         | Degraded _ -> not (hook_removal_abandoned ())
         | Available
         | Suspended
         | Resuming
         | ShutDown -> false)
        || ViewNavigationState.hook_owns_any_button state
        || RightClickTransitions.owns_button right_click
        || Option.isSome raw_navigation
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

    match stop_raw_navigation () with
    | Ok() -> ()
    | Error cleanupError -> Debug.WriteLine $"RhinosCanFly raw navigation cleanup: {cleanupError}"

    try
        apply_poll_requirement ()
    with timerError ->
        log_exception "mouse override recovery timer" timerError

let activate_available () =
    try
        state.lifecycle <- Available

        match reconcile_raw_navigation () with
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
            ViewNavigationState.gesture_navigation_engaged state
            || ViewNavigationState.view_latch_engaged state
            || ValueOption.isSome (RightClickTransitions.direct_navigation_host right_click)

        let mutable navigationCleanupFailed = false

        try
            SideButtonTransitions.process_hook_events state
            prune_released_side_buttons ()
            RightClickTransitions.prune_released_button right_click
            RightClickTransitions.update state right_click (command_depth > 0)

            let foreground = ViewNavigationState.foreground_root_window ()

            let navigationLostFocus =
                match active_navigation_host () with
                | ValueSome expected -> foreground <> expected.root_window
                | ValueNone -> false

            if navigationLostFocus then
                match release_navigation () with
                | Ok() -> ()
                | Error error ->
                    navigationCleanupFailed <- true
                    Debug.WriteLine $"RhinosCanFly mouse override focus loss: {error}"
            elif state.navigation_exit_requested || ViewNavigationState.exit_key_down state then
                match release_navigation () with
                | Ok() -> ()
                | Error error ->
                    navigationCleanupFailed <- true
                    Debug.WriteLine $"RhinosCanFly mouse override exit: {error}"
            else
                SideButtonTransitions.poll state

                ViewLatchTransitions.update state

            if not navigationCleanupFailed then
                match reconcile_raw_navigation () with
                | Ok() -> ()
                | Error error -> failwith error

            if
                navigationWasActive
                && not (ViewNavigationState.gesture_navigation_engaged state)
                && not (ViewNavigationState.view_latch_engaged state)
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

            // This poll runs during navigation. Keep reference tuples out of it.
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

state.poll_timer.Tick.Add(fun (_: EventArgs) -> poll_timer_elapsed ())

let keeps_navigation_active (commandName: string) =
    String.Equals(commandName, "RhinosCanFlyPivot", StringComparison.Ordinal)
    || String.Equals(commandName, "RhinosCanFlyPan", StringComparison.Ordinal)

let command_began =
    EventHandler<CommandEventArgs>(fun (_: obj) (event: CommandEventArgs) ->
        command_depth <- command_depth + 1

        try
            RightClickTransitions.clear_action right_click

            if
                not (keeps_navigation_active event.CommandEnglishName)
                && state.lifecycle = Available
                && (state.pending_side_button_events.Count > 0
                    || ViewNavigationState.hook_owns_any_button state
                    || RightClickTransitions.owns_button right_click
                    || ViewNavigationState.gesture_navigation_engaged state
                    || ViewNavigationState.view_latch_engaged state
                    || Option.isSome raw_navigation)
            then
                match release_navigation () with
                | Ok() -> ()
                | Error error -> Debug.WriteLine $"RhinosCanFly command navigation cleanup: {error}"

                apply_poll_requirement ()
                request_ui_redraw ()
        with error ->
            log_exception "command navigation callback" error)

let command_ended =
    EventHandler<CommandEventArgs>(fun (_: obj) (_: CommandEventArgs) ->
        if command_depth > 0 then
            command_depth <- command_depth - 1

        match mouse_hook_state with
        | HookInstalled _ ->
            try
                let document = RhinoDoc.ActiveDoc

                if not (isNull document) then
                    let view = document.Views.ActiveView

                    if not (isNull view) && view.Handle <> nativeint 0 then
                        update_right_click_viewport view
            with error ->
                log_exception "active viewport refresh" error
        | HookAbsent
        | HookRemovalPending _
        | HookRemovalAbandoned _ -> ())

do Command.BeginCommand.AddHandler command_began
do Command.EndCommand.AddHandler command_ended

let start_view_latch (view: RhinoView) (mode: ViewNavigationMode) (completion: Action option) =
    if isNull view || isNull view.Document || view.Handle = nativeint 0 then
        Error "The active viewport is unavailable."
    else
        let host = capture_viewport_host view
        let originalTarget = view.ActiveViewport.CameraTarget

        match ViewLatchTransitions.start_or_switch state host mode completion with
        | Error error -> Error error
        | Ok() ->
            let activation =
                match reconcile_raw_navigation () with
                | Error error -> Error error
                | Ok() -> refresh_mouse_hook ()

            match activation with
            | Ok() -> Ok()
            | Error activationError ->
                let mutable error = activationError

                match ViewLatchTransitions.release state with
                | Ok() -> ()
                | Error cleanupError -> error <- $"{error}; cleanup failed: {cleanupError}"

                match reconcile_raw_navigation () with
                | Ok() -> ()
                | Error cleanupError -> error <- $"{error}; raw cleanup failed: {cleanupError}"

                try
                    if view_matches_host host view then
                        view.ActiveViewport.SetCameraTarget(originalTarget, false)
                with targetError ->
                    error <- $"{error}; target rollback failed: {targetError.Message}"

                Error error

let stop_view_latch (mode: ViewNavigationMode) =
    let wasActive = ViewLatchTransitions.is_mode state mode
    let navigationResult = ViewLatchTransitions.stop state mode
    let rawResult = reconcile_raw_navigation ()

    let result =
        match navigationResult with
        | Error error ->
            match rawResult with
            | Error rawError -> Error $"{error}; raw cleanup failed: {rawError}"
            | Ok() -> Error error
        | Ok() -> rawResult

    match refresh_mouse_hook () with
    | Ok() -> ()
    | Error error -> Debug.WriteLine $"RhinosCanFly mouse override hook: {error}"

    apply_poll_requirement ()

    if wasActive && not (ViewNavigationState.view_latch_engaged state) then
        request_ui_redraw ()

    result

let view_latch_is (mode: ViewNavigationMode) = ViewLatchTransitions.is_mode state mode

let apply (config: MouseOverrideConfig) =
    if state.lifecycle = ShutDown then
        Error "Mouse button overrides have already shut down."
    else
        RightClickTransitions.clear_action right_click

        match release_navigation () with
        | Error error ->
            activate_degraded error
            Error error
        | Ok() ->
            state.routing <-
                { runtime_enabled = config.runtime_enabled
                  mouse4 = config.mouse4
                  mouse5 = config.mouse5
                  right_click_entry = config.right_click_entry
                  default_flight_mode = config.default_flight_mode
                  parallel_view_flying = config.parallel_view_flying
                  parallel_right_click_entry = config.parallel_right_click_entry
                  shift_right_click = config.shift_right_click
                  alt_right_click = config.alt_right_click
                  ctrl_right_click = config.ctrl_right_click
                  exit = config.exit_binding
                  exit_on_mouse_left = config.exit_on_left
                  exit_on_mouse_right = config.exit_on_right
                  prepare_navigation = config.prepare_navigation
                  retarget = config.retarget }

            if state.lifecycle = Suspended then
                Ok()
            else
                state.lifecycle <- Resuming

                try
                    match refresh_mouse_hook () with
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
        RightClickTransitions.clear_action right_click

        try
            match release_navigation () with
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
            match refresh_mouse_hook () with
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

    match release_navigation () with
    | Ok() -> ()
    | Error error -> errors.Add $"view navigation: {error}"

    match mouse_hook_state with
    | HookRemovalPending _
    | HookRemovalAbandoned _ ->
        match remove_mouse_hook () with
        | Ok() -> ()
        | Error error -> errors.Add $"mouse hook: {error}"
    | HookAbsent
    | HookInstalled _ -> ()

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
        attempt "application initialized handler" (fun () -> RhinoApp.Initialized.RemoveHandler application_initialized)

        attempt "view navigation" (fun () ->
            match release_navigation () with
            | Ok() -> ()
            | Error error -> Debug.WriteLine $"RhinosCanFly mouse override shutdown: {error}")

        attempt "side-button ownership" (fun () ->
            ViewNavigationState.set_hook_button_ownership state Mouse4 NotOwned
            ViewNavigationState.set_hook_button_ownership state Mouse5 NotOwned)

        attempt "mouse hook" (fun () ->
            match remove_mouse_hook () with
            | Ok() -> ()
            | Error error -> failwith error)

        attempt "timer" (fun () -> state.poll_timer.Dispose())
