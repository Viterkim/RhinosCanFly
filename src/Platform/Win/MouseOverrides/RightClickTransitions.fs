module RhinosCanFly.Platform.Win.RightClickTransitions

open System
open System.Diagnostics
open System.Drawing
open Rhino
open Rhino.Display
open RhinosCanFly
open RhinosCanFly.Platform.Win.MouseOverrideTypes

type FlyEntry =
    { host: ViewportHostIdentity
      entry_mode: RightClickEntryMode
      default_flight_mode: DefaultFlightMode }

type NavigationClick =
    { host: ViewportHostIdentity
      action: RoutedMouseAction
      screen_point: Point }

type RightClickAction =
    | EnterFlight of FlyEntry
    | NavigateView of NavigationClick
    | StopNavigation
    | PanParallel of ViewportHostIdentity
    | ZoomParallel of ViewportHostIdentity

type RightClickGesture =
    | Idle
    | NativeModifiedGesture
    | ButtonDown of RightClickAction
    | ButtonDownHandled of RightClickAction
    | ButtonReleasedBeforeHandling of RightClickAction
    | ButtonReleased of RightClickAction
    | FlightDispatched

type RightClickState =
    { mutable gesture: RightClickGesture
      mutable button_ownership: HookButtonOwnership
      mutable pair_id: int64
      mutable dispatched_entry: struct (FlyEntry * int64 * Guid * int64) option }

[<Struct>]
type EntryPreparation =
    | EntryReady of RhinoView
    | EntryDeferred
    | EntryUnavailable

[<Struct>]
type RightClickViewport =
    { host: ViewportHostIdentity
      name: string
      is_perspective: bool
      is_parallel: bool }

let create () =
    { gesture = Idle
      button_ownership = NotOwned
      pair_id = 0L
      dispatched_entry = None }

let try_wake (navigation: State) =
    try
        MouseOverrideState.keep_timer_running navigation
        true
    with error ->
        Debug.WriteLine $"RhinosCanFly right-click timer: {error}"
        false

let entry_enabled (navigation: State) =
    match navigation.routing.actions.right_click_entry with
    | RightClickEntryMode.ClickToFly
    | RightClickEntryMode.ClickToFlyDuringCommands
    | RightClickEntryMode.HoldToFly
    | RightClickEntryMode.HoldToFlyDuringCommands -> true
    | RightClickEntryMode.Off
    | _ -> false

let entry_during_commands (mode: RightClickEntryMode) =
    match mode with
    | RightClickEntryMode.ClickToFlyDuringCommands
    | RightClickEntryMode.HoldToFlyDuringCommands -> true
    | RightClickEntryMode.Off
    | RightClickEntryMode.ClickToFly
    | RightClickEntryMode.HoldToFly
    | _ -> false

let entry_while_held (mode: RightClickEntryMode) =
    match mode with
    | RightClickEntryMode.HoldToFly
    | RightClickEntryMode.HoldToFlyDuringCommands -> true
    | RightClickEntryMode.Off
    | RightClickEntryMode.ClickToFly
    | RightClickEntryMode.ClickToFlyDuringCommands
    | _ -> false

let action_pending (state: RightClickState) =
    match state.gesture with
    | Idle -> Option.isSome state.dispatched_entry
    | NativeModifiedGesture -> false
    | ButtonDownHandled _ -> false
    | ButtonDown _
    | ButtonReleasedBeforeHandling _
    | ButtonReleased _
    | FlightDispatched -> true

let clear_action (state: RightClickState) =
    state.gesture <- Idle
    state.dispatched_entry <- None

let owns_button (state: RightClickState) =
    match state.button_ownership with
    | NotOwned -> false
    | Owned
    | ReleaseObserved -> true

let navigation_active (navigation: State) =
    MouseOverrideState.gesture_navigation_engaged navigation
    || MouseOverrideState.view_latch_engaged navigation

let navigation_exit_requested (navigation: State) =
    navigation_active navigation
    && MouseOverrideState.right_mouse_exit_requested navigation

let navigation_exit_capture_needed (navigation: State) =
    navigation_active navigation
    && MouseOverrideState.right_mouse_exit_capture_needed navigation

let capture_needed (navigation: State) (state: RightClickState) =
    ViewportNameList.has_allowed_viewports navigation.routing.actions.viewport_capabilities
    || navigation_exit_capture_needed navigation
    || owns_button state
    || action_pending state

let requested_gesture_action (navigation: State) (modifiers: MouseModifiers) =
    let configured_action =
        match modifiers.shift, modifiers.alt, modifiers.control with
        | true, false, false -> navigation.routing.actions.shift_right_click
        | false, true, false -> navigation.routing.actions.alt_right_click
        | false, false, true -> navigation.routing.actions.ctrl_right_click
        | _ -> RoutedMouseAction.Off

    if RoutedMouseAction.enabled configured_action then
        ValueSome configured_action
    else
        ValueNone

let right_click_flight_entry_allowed (navigation: State) (viewport_name: string) =
    MouseOverrideState.capabilities_allowed navigation viewport_name
    && ViewportNameList.allows viewport_name navigation.routing.actions.right_click_flight_entry

let action
    (navigation: State)
    (viewport: RightClickViewport)
    (screen_point: Point)
    (modifiers: MouseModifiers)
    (command_active: bool)
    =
    let host = viewport.host

    let capabilities_allowed =
        MouseOverrideState.capabilities_allowed navigation viewport.name

    let requested_action =
        if capabilities_allowed then
            requested_gesture_action navigation modifiers
        else
            ValueNone

    match requested_action with
    | ValueSome gesture_action ->
        ValueSome(
            NavigateView
                { host = host
                  action = gesture_action
                  screen_point = screen_point }
        )
    | ValueNone when navigation_active navigation && navigation_exit_requested navigation -> ValueSome StopNavigation
    | ValueNone when navigation_active navigation -> ValueNone
    | ValueNone when
        capabilities_allowed
        && viewport.is_parallel
        && modifiers.shift
        && not modifiers.alt
        && not modifiers.control
        ->
        // Off still keeps Shift pan and Alt zoom in parallel views.
        ValueSome(PanParallel host)
    | ValueNone when
        capabilities_allowed
        && viewport.is_parallel
        && modifiers.alt
        && not modifiers.shift
        && not modifiers.control
        ->
        ValueSome(ZoomParallel host)
    | ValueNone when
        entry_enabled navigation
        && right_click_flight_entry_allowed navigation viewport.name
        && (viewport.is_perspective || viewport.is_parallel)
        && (entry_during_commands navigation.routing.actions.right_click_entry
            || not command_active)
        && not modifiers.shift
        && not modifiers.alt
        && not modifiers.control
        ->
        ValueSome(
            EnterFlight
                { host = host
                  entry_mode = navigation.routing.actions.right_click_entry
                  default_flight_mode = navigation.routing.actions.default_flight_mode }
        )
    | ValueNone -> ValueNone

let rec handle_event
    (navigation: State)
    (state: RightClickState)
    (try_view: nativeint -> RightClickViewport voption)
    (command_active: bool)
    (event: Win32.MouseHookEvent)
    =
    let is_down =
        event.message = Win32Native.WM_RBUTTONDOWN
        || event.message = Win32Native.WM_RBUTTONDBLCLK

    let is_up = event.message = Win32Native.WM_RBUTTONUP

    if is_up && state.gesture = NativeModifiedGesture then
        clear_action state
        false
    elif is_down && state.gesture = NativeModifiedGesture then
        clear_action state
        handle_event navigation state try_view command_active event
    elif is_up && owns_button state then
        state.button_ownership <- NotOwned
        state.dispatched_entry <- None

        match state.gesture with
        | ButtonDown(EnterFlight entry) when entry_while_held entry.entry_mode -> clear_action state
        | ButtonDown(EnterFlight _ as captured) -> state.gesture <- ButtonReleased captured
        | ButtonDown captured -> state.gesture <- ButtonReleasedBeforeHandling captured
        | ButtonDownHandled captured -> state.gesture <- ButtonReleased captured
        | FlightDispatched -> clear_action state
        | Idle
        | NativeModifiedGesture
        | ButtonReleasedBeforeHandling _
        | ButtonReleased _ -> ()

        if not (try_wake navigation) then
            clear_action state

        true
    elif is_down && owns_button state then
        if state.button_ownership = ReleaseObserved then
            state.button_ownership <- NotOwned
            clear_action state
            handle_event navigation state try_view command_active event
        else
            true
    elif is_down && action_pending state then
        match state.gesture with
        | ButtonReleasedBeforeHandling _
        | ButtonReleased _ ->
            state.button_ownership <- NotOwned
            clear_action state
            handle_event navigation state try_view command_active event
        | ButtonDown _
        | FlightDispatched ->
            state.pair_id <- state.pair_id + 1L
            state.button_ownership <- Owned

            if not (try_wake navigation) then
                clear_action state

            true
        | Idle
        | NativeModifiedGesture
        | ButtonDownHandled _ -> false
    elif
        not is_down
        || navigation.lifecycle <> Available
        || Win32Native.GetCapture() <> nativeint 0
    then
        false
    else
        let current_modifiers = event.modifiers

        match try_view event.hook_window with
        | ValueSome hook_viewport ->
            match try_view event.point_window with
            | ValueSome point_viewport when MouseOverrideState.same_host hook_viewport.host point_viewport.host ->
                match action navigation point_viewport event.screen_point current_modifiers command_active with
                | ValueNone when
                    not (navigation_active navigation)
                    && (current_modifiers.shift || current_modifiers.alt || current_modifiers.control)
                    ->
                    state.gesture <- NativeModifiedGesture
                    false
                | ValueNone -> false
                | ValueSome captured ->
                    state.pair_id <- state.pair_id + 1L
                    state.button_ownership <- Owned
                    state.gesture <- ButtonDown captured

                    if not (try_wake navigation) then
                        clear_action state

                    true
            | ValueSome _
            | ValueNone -> false
        | ValueNone -> false

let entry_command (entry: FlyEntry) =
    let flight_mode = DefaultFlightMode.flight_mode entry.default_flight_mode

    if entry_while_held entry.entry_mode then
        match flight_mode with
        | FlightMode.Temporary -> "'_RhinosCanFlyTempFlyHeld"
        | FlightMode.Normal
        | _ -> "'_RhinosCanFlyHeld"
    else
        match flight_mode with
        | FlightMode.Temporary -> "'_RhinosCanFlyTempFly"
        | FlightMode.Normal
        | _ -> "'_RhinosCanFly"

let try_entry_view (entry: FlyEntry) =
    let view = RhinoView.FromRuntimeSerialNumber entry.host.view_serial_number
    let document = if isNull view then null else view.Document
    let active_document = RhinoDoc.ActiveDoc
    let (ViewWindowHandle expected_window) = entry.host.view_window

    if
        isNull view
        || isNull document
        || document.RuntimeSerialNumber <> entry.host.document_serial_number
        || isNull active_document
        || active_document.RuntimeSerialNumber <> entry.host.document_serial_number
        || view.Handle <> expected_window
        || view.ActiveViewportID <> entry.host.viewport_id
        || MouseOverrideState.root_window view.Handle <> entry.host.root_window
    then
        ValueNone
    else
        ValueSome view

let try_prepare_entry_view (entry: FlyEntry) =
    let foreground_ready =
        MouseOverrideState.foreground_root_window () = entry.host.root_window
        || MouseOverrideState.try_bring_root_window_to_foreground entry.host.root_window

    if not foreground_ready then
        EntryUnavailable
    else
        match try_entry_view entry with
        | ValueNone -> EntryUnavailable
        | ValueSome view ->
            let active_view = view.Document.Views.ActiveView

            if
                isNull active_view
                || active_view.RuntimeSerialNumber <> view.RuntimeSerialNumber
            then
                view.Document.Views.ActiveView <- view
                EntryDeferred
            else
                EntryReady view

let dispatch_entry_with (dispatch: uint32 -> string -> bool) (state: RightClickState) (entry: FlyEntry) =
    let request_id = Guid.NewGuid()
    let pair_id = state.pair_id
    state.gesture <- FlightDispatched
    let held = entry_while_held entry.entry_mode

    state.dispatched_entry <-
        if held then
            Some(struct (entry, state.pair_id, request_id, Stopwatch.GetTimestamp() + 2L * Stopwatch.Frequency))
        else
            None

    let command =
        if held then
            let name = (entry_command entry).Replace("'_", "'_-")
            $"{name} {request_id:N}"
        else
            entry_command entry

    let clear_dispatched_request () =
        match state.dispatched_entry with
        | Some(struct (_, _, pending_id, _)) when pending_id = request_id -> clear_action state
        | None when not held && state.pair_id = pair_id && state.gesture = FlightDispatched -> clear_action state
        | _ -> ()

    try
        if not (dispatch entry.host.document_serial_number command) then
            clear_dispatched_request ()
            Debug.WriteLine "RhinosCanFly right-click flight command was rejected by Rhino."
        elif not held && state.gesture = FlightDispatched then
            clear_dispatched_request ()
    with _ ->
        clear_dispatched_request ()
        reraise ()

let dispatch_entry (state: RightClickState) (entry: FlyEntry) =
    dispatch_entry_with
        (fun (document: uint32) (command: string) -> RhinoApp.RunScript(document, command, false))
        state
        entry

let consume_held_entry (state: RightClickState) (host: ViewportHostIdentity) (request_id: Guid) =
    match state.dispatched_entry with
    | Some(struct (entry, pair_id, pending_id, deadline)) when request_id = pending_id ->
        clear_action state

        if
            entry.host = host
            && pair_id = state.pair_id
            && entry_while_held entry.entry_mode
            && state.button_ownership = Owned
            && Stopwatch.GetTimestamp() < deadline
        then
            Some pair_id
        else
            None
    | _ -> None

let projection_allows_entry (navigation: State) (view: RhinoView) =
    let viewport = view.ActiveViewport

    right_click_flight_entry_allowed navigation viewport.Name
    && (viewport.IsPerspectiveProjection || viewport.IsParallelProjection)

let apply_navigation_click (navigation: State) (click: NavigationClick) =
    if navigation.lifecycle <> Available then
        GestureNavigationTransitions.Failed "Mouse button overrides are unavailable."
    else
        GestureNavigationTransitions.press
            navigation
            GestureOwner.ModifiedRightClick
            click.action
            click.host
            click.screen_point

let try_dispatch_entry (navigation: State) (state: RightClickState) (command_active: bool) (entry: FlyEntry) =
    if navigation.lifecycle <> Available then
        clear_action state
    else
        let pair_id = state.pair_id
        let pending_gesture = state.gesture

        match try_prepare_entry_view entry with
        | _ when
            state.pair_id <> pair_id
            || state.gesture <> pending_gesture
            || navigation.lifecycle <> Available
            ->
            ()
        | EntryUnavailable -> clear_action state
        | EntryDeferred -> ()
        | EntryReady view ->
            if not (projection_allows_entry navigation view) then
                clear_action state
            else
                let can_enter = entry_during_commands entry.entry_mode || not command_active

                if not can_enter then
                    clear_action state
                elif view.MouseCaptured false then
                    ()
                elif entry_while_held entry.entry_mode then
                    if Win32.key_down Win32Native.VK_RBUTTON then
                        dispatch_entry state entry
                    else
                        clear_action state
                else
                    dispatch_entry state entry

let update (navigation: State) (state: RightClickState) (command_active: bool) =
    match state.gesture with
    | NativeModifiedGesture -> ()
    | FlightDispatched
    | Idle ->
        let invalid =
            match state.dispatched_entry with
            | Some(struct (entry, pair_id, _, deadline)) ->
                state.button_ownership <> Owned
                || pair_id <> state.pair_id
                || Stopwatch.GetTimestamp() >= deadline
                || MouseOverrideState.foreground_root_window () <> entry.host.root_window
                || ValueOption.isNone (try_entry_view entry)
            | None -> true

        if navigation.lifecycle <> Available || invalid then
            clear_action state
    | ButtonDown(NavigateView click) ->
        match apply_navigation_click navigation click with
        | GestureNavigationTransitions.Applied
        | GestureNavigationTransitions.Retargeted _ -> state.gesture <- ButtonDownHandled(NavigateView click)
        | GestureNavigationTransitions.Deferred -> ()
        | GestureNavigationTransitions.Failed error ->
            Debug.WriteLine $"RhinosCanFly mouse action: {error}"
            clear_action state
    | ButtonDown StopNavigation ->
        navigation.navigation_exit_requested <- true
        MouseOverrideState.keep_timer_running navigation
        state.gesture <- ButtonDownHandled StopNavigation
    | ButtonDown(PanParallel _)
    | ButtonDown(ZoomParallel _)
    | ButtonDownHandled(PanParallel _)
    | ButtonDownHandled(ZoomParallel _) when state.button_ownership = ReleaseObserved -> clear_action state
    | ButtonDown(PanParallel host) ->
        match GestureNavigationTransitions.prepare_action_view host with
        | GestureNavigationTransitions.ActionViewReady(_, active_host) ->
            state.gesture <- ButtonDownHandled(PanParallel active_host)
        | GestureNavigationTransitions.ActionViewDeferred -> ()
        | GestureNavigationTransitions.ActionViewUnavailable error ->
            Debug.WriteLine $"RhinosCanFly parallel pan: {error}"
            clear_action state
    | ButtonDown(ZoomParallel host) ->
        match GestureNavigationTransitions.prepare_action_view host with
        | GestureNavigationTransitions.ActionViewReady(_, active_host) ->
            state.gesture <- ButtonDownHandled(ZoomParallel active_host)
        | GestureNavigationTransitions.ActionViewDeferred -> ()
        | GestureNavigationTransitions.ActionViewUnavailable error ->
            Debug.WriteLine $"RhinosCanFly parallel zoom: {error}"
            clear_action state
    | ButtonDownHandled _ -> ()
    | ButtonReleasedBeforeHandling(NavigateView click) ->
        match apply_navigation_click navigation click with
        | GestureNavigationTransitions.Applied
        | GestureNavigationTransitions.Retargeted _ ->
            GestureNavigationTransitions.release navigation GestureOwner.ModifiedRightClick
            clear_action state
        | GestureNavigationTransitions.Deferred -> ()
        | GestureNavigationTransitions.Failed error ->
            Debug.WriteLine $"RhinosCanFly mouse action: {error}"
            clear_action state
    | ButtonReleasedBeforeHandling StopNavigation ->
        navigation.navigation_exit_requested <- true
        MouseOverrideState.keep_timer_running navigation
        clear_action state
    | ButtonReleasedBeforeHandling(PanParallel _)
    | ButtonReleasedBeforeHandling(ZoomParallel _) -> clear_action state
    | ButtonReleasedBeforeHandling(EnterFlight entry) -> try_dispatch_entry navigation state command_active entry
    | ButtonDown(EnterFlight entry) when entry_while_held entry.entry_mode ->
        try_dispatch_entry navigation state command_active entry
    | ButtonDown(EnterFlight _) -> ()
    | ButtonReleased(NavigateView _) ->
        GestureNavigationTransitions.release navigation GestureOwner.ModifiedRightClick
        clear_action state
    | ButtonReleased StopNavigation -> clear_action state
    | ButtonReleased(PanParallel _) -> clear_action state
    | ButtonReleased(ZoomParallel _) -> clear_action state
    | ButtonReleased(EnterFlight entry) -> try_dispatch_entry navigation state command_active entry

let command_began (state: RightClickState) (command_name: string) =
    let expected_held_command =
        match state.dispatched_entry with
        | Some(struct (entry, _, _, _)) -> entry_command entry = $"'_{command_name}"
        | None -> false

    match state.gesture with
    | _ when expected_held_command -> state.gesture <- Idle
    | ButtonDown(EnterFlight entry)
    | ButtonReleasedBeforeHandling(EnterFlight entry)
    | ButtonReleased(EnterFlight entry) when entry_during_commands entry.entry_mode -> ()
    | Idle
    | NativeModifiedGesture
    | ButtonDown _
    | ButtonDownHandled _
    | ButtonReleasedBeforeHandling _
    | ButtonReleased _
    | FlightDispatched -> clear_action state

let observe_physical_button (state: RightClickState) (down: bool) =
    if state.button_ownership = Owned && not down then
        state.button_ownership <- ReleaseObserved
        state.dispatched_entry <- None

let reconcile_physical_button (state: RightClickState) =
    observe_physical_button state (Win32.key_down Win32Native.VK_RBUTTON)

let parallel_zoom_host (state: RightClickState) =
    match state.gesture with
    | ButtonDownHandled(ZoomParallel host) ->
        match state.button_ownership with
        | Owned -> ValueSome host
        | ReleaseObserved
        | NotOwned -> ValueNone
    | Idle
    | NativeModifiedGesture
    | ButtonDown _
    | ButtonDownHandled _
    | ButtonReleasedBeforeHandling _
    | ButtonReleased _
    | FlightDispatched -> ValueNone

let parallel_pan_host (state: RightClickState) =
    match state.gesture with
    | ButtonDownHandled(PanParallel host) ->
        match state.button_ownership with
        | Owned -> ValueSome host
        | ReleaseObserved
        | NotOwned -> ValueNone
    | Idle
    | NativeModifiedGesture
    | ButtonDown _
    | ButtonDownHandled _
    | ButtonReleasedBeforeHandling _
    | ButtonReleased _
    | FlightDispatched -> ValueNone

let direct_navigation_host (state: RightClickState) =
    match parallel_zoom_host state with
    | ValueSome host -> ValueSome host
    | ValueNone -> parallel_pan_host state

let clear_direct_navigation (state: RightClickState) =
    match state.gesture with
    | ButtonDown(PanParallel _)
    | ButtonDown(ZoomParallel _)
    | ButtonDownHandled(PanParallel _)
    | ButtonDownHandled(ZoomParallel _)
    | ButtonReleasedBeforeHandling(PanParallel _)
    | ButtonReleasedBeforeHandling(ZoomParallel _)
    | ButtonReleased(PanParallel _)
    | ButtonReleased(ZoomParallel _) -> clear_action state
    | Idle
    | NativeModifiedGesture
    | ButtonDown _
    | ButtonDownHandled _
    | ButtonReleasedBeforeHandling _
    | ButtonReleased _
    | FlightDispatched -> ()

let reset (state: RightClickState) =
    clear_action state
    state.button_ownership <- NotOwned
