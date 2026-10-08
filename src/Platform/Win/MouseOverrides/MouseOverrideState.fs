module RhinosCanFly.Platform.Win.MouseOverrideState

open System.Diagnostics
open RhinosCanFly
open RhinosCanFly.Platform.Win.MouseOverrideTypes

let observe_active_host (state: State) (host: ViewportHostIdentity voption) =
    if state.active_host <> host then
        state.active_host <- host
        state.host_revision <- state.host_revision + 1L

let start_admission_deadline (now: int64) (admission: MouseAdmission) =
    if admission.deadline = 0L then
        admission.deadline <- now + 2L * Stopwatch.Frequency

let admission_is_current
    (now: int64)
    (foreground: RootWindow)
    (state: State)
    (host: ViewportHostIdentity)
    (admission: MouseAdmission)
    =
    (admission.deadline = 0L || now < admission.deadline)
    && (foreground = host.root_window
        || (not admission.activation_attempted && foreground = admission.foreground))
    && (state.host_revision = admission.host_revision
        || (not admission.activation_attempted
            && state.host_revision = admission.host_revision + 1L
            && state.active_host = ValueSome host))

let apply_suspended_routing (state: State) (config: MouseOverrideConfig) =
    match state.suspension_cleanup_error with
    | Some error -> Error error
    | None when state.suspension_ids.Count = 0 -> Error "Input is not suspended."
    | None ->
        state.routing <- config
        state.lifecycle <- Suspended
        Ok()

let hook_button_ownership (state: State) (button: SideButton) =
    match button with
    | Middle -> state.side_button_hook_capture.middle
    | Mouse4 -> state.side_button_hook_capture.mouse4
    | Mouse5 -> state.side_button_hook_capture.mouse5

let hook_owns_button (state: State) (button: SideButton) =
    match hook_button_ownership state button with
    | NotOwned -> false
    | Owned
    | ReleaseObserved -> true

let set_hook_button_ownership (state: State) (button: SideButton) (ownership: HookButtonOwnership) =
    match button with
    | Middle -> state.side_button_hook_capture.middle <- ownership
    | Mouse4 -> state.side_button_hook_capture.mouse4 <- ownership
    | Mouse5 -> state.side_button_hook_capture.mouse5 <- ownership

let observe_hook_button_released (state: State) (button: SideButton) =
    match hook_button_ownership state button with
    | NotOwned -> ()
    | Owned -> set_hook_button_ownership state button ReleaseObserved
    | ReleaseObserved -> ()

let hook_owns_any_button (state: State) =
    hook_owns_button state Middle
    || hook_owns_button state Mouse4
    || hook_owns_button state Mouse5

let raw_mouse_buttons_owned (state: State) (right_owned: bool) (is_down: int -> bool) =
    not (is_down Win32Native.VK_LBUTTON)
    && (not (is_down Win32Native.VK_RBUTTON) || right_owned)
    && (not (is_down Win32Native.VK_MBUTTON)
        || hook_button_ownership state Middle = Owned)
    && (not (is_down Win32Native.VK_XBUTTON1)
        || hook_button_ownership state Mouse4 = Owned)
    && (not (is_down Win32Native.VK_XBUTTON2)
        || hook_button_ownership state Mouse5 = Owned)

let action_for (state: State) (button: SideButton) =
    match button with
    | Middle -> state.routing.actions.middle
    | Mouse4 -> state.routing.actions.mouse4
    | Mouse5 -> state.routing.actions.mouse5

let capabilities_allowed (state: State) (viewport_name: string) =
    ViewportNameList.allows viewport_name state.routing.actions.viewport_capabilities

let side_button_routing_enabled (state: State) =
    ViewportNameList.has_allowed_viewports state.routing.actions.viewport_capabilities
    && (RoutedMouseAction.enabled state.routing.actions.middle
        || RoutedMouseAction.enabled state.routing.actions.mouse4
        || RoutedMouseAction.enabled state.routing.actions.mouse5)

let gesture_navigation_engaged (state: State) =
    match state.gesture_navigation with
    | NoGestureNavigation -> false
    | GestureNavigationActive _ -> true

let view_latch_engaged (state: State) =
    match state.view_latch with
    | NoViewLatch -> false
    | WaitingForRelease _
    | ViewLatchActive _ -> true

let exit_key_is_down (state: State) (virtual_key: int) =
    if virtual_key = Win32Native.VK_RBUTTON then
        match state.view_latch with
        | WaitingForRelease _ -> false
        | NoViewLatch
        | ViewLatchActive _ -> Win32.key_down virtual_key
    else
        Win32.key_down virtual_key

let exit_keys_down (state: State) (keys: VirtualKey array) =
    let mutable index = 0
    let mutable down = keys.Length > 0

    while down && index < keys.Length do
        let (VirtualKey key) = keys[index]
        down <- exit_key_is_down state key
        index <- index + 1

    down

let exit_key_down (state: State) =
    match state.routing.exit_binding with
    | Some binding -> exit_keys_down state binding.virtual_keys
    | None -> false

let binding_contains_key (virtual_key: int) (keys: VirtualKey array) =
    let mutable index = 0
    let mutable found = false

    while not found && index < keys.Length do
        let (VirtualKey key) = keys[index]
        found <- key = virtual_key
        index <- index + 1

    found

let exit_binding_contains (state: State) (virtual_key: int) =
    match state.routing.exit_binding with
    | Some binding -> binding_contains_key virtual_key binding.virtual_keys
    | None -> false

let right_mouse_exit_capture_needed (state: State) =
    state.routing.actions.exit_on_mouse_right
    || exit_binding_contains state Win32Native.VK_RBUTTON

let right_mouse_exit_requested (state: State) =
    state.routing.actions.exit_on_mouse_right || exit_key_down state

let shift_down () =
    Win32Native.GetAsyncKeyState Win32Native.VK_SHIFT < 0s
    || Win32Native.GetAsyncKeyState Win32Native.VK_LSHIFT < 0s
    || Win32Native.GetAsyncKeyState Win32Native.VK_RSHIFT < 0s

let alt_down () =
    Win32Native.GetAsyncKeyState Win32Native.VK_MENU < 0s
    || Win32Native.GetAsyncKeyState Win32Native.VK_LMENU < 0s
    || Win32Native.GetAsyncKeyState Win32Native.VK_RMENU < 0s

let control_down () =
    Win32Native.GetAsyncKeyState Win32Native.VK_CONTROL < 0s
    || Win32Native.GetAsyncKeyState Win32Native.VK_LCONTROL < 0s
    || Win32Native.GetAsyncKeyState Win32Native.VK_RCONTROL < 0s

let same_host (left: ViewportHostIdentity) (right: ViewportHostIdentity) =
    left.document_serial_number = right.document_serial_number
    && left.view_serial_number = right.view_serial_number
    && left.viewport_id = right.viewport_id
    && left.view_window = right.view_window
    && left.root_window = right.root_window

let begin_action (state: State) =
    MouseFlightEntry.revoke ()
    state.pending_flight_entry <- None
    state.navigation_revision <- state.navigation_revision + 1L
    let revision = state.navigation_revision

    fun () ->
        state.lifecycle = Available
        && not state.navigation_exit_requested
        && state.navigation_revision = revision

let keep_timer_running (state: State) =
    state.poll_timer.Interval <- POLL_TIMER_INTERVAL_MILLISECONDS

    if not state.poll_timer.Enabled then
        state.poll_timer.Start()

let keep_watchdog_running (state: State) =
    state.poll_timer.Interval <- POLL_TIMER_WATCHDOG_INTERVAL_MILLISECONDS

    if not state.poll_timer.Enabled then
        state.poll_timer.Start()

let fast_poll_required (state: State) =
    Option.isSome state.pending_flight_entry
    || state.pending_side_button_events.Count > 0
    || state.navigation_exit_requested
    || (state.lifecycle = Available
        && (gesture_navigation_engaged state || view_latch_engaged state))

let stop_timer_if_idle (state: State) =
    if
        not (gesture_navigation_engaged state)
        && not (view_latch_engaged state)
        && Option.isNone state.pending_flight_entry
    then
        state.poll_timer.Stop()

let root_window (window: nativeint) =
    let root = Win32Native.GetAncestor(window, Win32Native.GA_ROOT)

    RootWindow(if root = nativeint 0 then window else root)

let foreground_root_window () =
    RootWindow(Win32Native.GetForegroundWindow())

let create_admission (state: State) =
    { foreground = foreground_root_window ()
      host_revision = state.host_revision
      activation_attempted = false
      deadline = Stopwatch.GetTimestamp() + 2L * Stopwatch.Frequency }

let try_bring_root_window_to_foreground (window: RootWindow) =
    if foreground_root_window () = window then
        true
    else
        let (RootWindow handle) = window

        handle <> nativeint 0
        && Win32Native.IsWindow handle
        && Win32Native.IsWindowEnabled handle
        && Win32Native.SetForegroundWindow handle
        && foreground_root_window () = window

let navigation_host (state: State) =
    match state.gesture_navigation with
    | GestureNavigationActive session -> ValueSome session.host
    | NoGestureNavigation ->
        match state.view_latch with
        | WaitingForRelease session
        | ViewLatchActive session -> ValueSome session.host
        | NoViewLatch -> ValueNone

let complete_gesture_navigation (revision: int64) (gesture: GestureNavigation) =
    match gesture with
    | NoGestureNavigation -> Ok()
    | GestureNavigationActive session ->
        let rollback = session.startup_rollback
        session.startup_rollback <- None

        try
            match rollback with
            | Some restore -> restore revision
            | None -> Ok()
        with error ->
            Error $"Could not restore the navigation target: {error.Message}"

let complete_view_latch (state: State) (revision: int64) (latch: ViewLatch) =
    let errors = ResizeArray<string>()

    match latch with
    | NoViewLatch -> ()
    | WaitingForRelease session
    | ViewLatchActive session ->
        let rollback = session.startup_rollback
        session.startup_rollback <- None

        match rollback with
        | Some restore ->
            try
                match restore revision with
                | Ok() -> ()
                | Error error -> errors.Add error
            with error ->
                errors.Add error.Message
        | None -> ()

        match session.completion with
        | Some completion ->
            try
                completion.Invoke(fun () ->
                    Option.isNone rollback
                    && state.navigation_revision = revision
                    && not (view_latch_engaged state)
                    && not (gesture_navigation_engaged state)
                    && state.lifecycle <> ShutDown)
            with error ->
                errors.Add $"Could not restore the original view: {error.Message}"
        | None -> ()

    if errors.Count = 0 then
        Ok()
    else
        Error(String.concat "; " errors)

let commit_view_latch (state: State) (host: ViewportHostIdentity) =
    match state.gesture_navigation with
    | GestureNavigationActive session when session.host = host -> session.startup_rollback <- None
    | _ -> ()

    match state.view_latch with
    | ViewLatchActive session when session.host = host -> session.startup_rollback <- None
    | _ -> ()

let clear_navigation (state: State) =
    let previous_view_latch = state.view_latch

    state.gesture_navigation <- NoGestureNavigation
    state.view_latch <- NoViewLatch
    state.pending_flight_entry <- None
    state.navigation_exit_requested <- false
    state.pending_side_button_events.Clear()
    previous_view_latch

let release_all (state: State) =
    state.navigation_revision <- state.navigation_revision + 1L
    let revision = state.navigation_revision
    let previous_gesture = state.gesture_navigation
    let previous_view_latch = clear_navigation state
    state.poll_timer.Stop()

    let gesture_result = complete_gesture_navigation revision previous_gesture
    let latch_result = complete_view_latch state revision previous_view_latch

    match gesture_result, latch_result with
    | Ok(), Ok() -> Ok()
    | Error error, Ok()
    | Ok(), Error error -> Error error
    | Error gesture_error, Error latch_error -> Error $"{gesture_error}; {latch_error}"
