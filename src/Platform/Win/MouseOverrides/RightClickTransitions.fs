module RhinosCanFly.Platform.Win.RightClickTransitions

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
      mutable button_ownership: HookButtonOwnership }

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
      button_ownership = NotOwned }

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
    | Idle -> false
    | NativeModifiedGesture -> false
    | ButtonDownHandled _ -> false
    | ButtonDown _
    | ButtonReleasedBeforeHandling _
    | ButtonReleased _
    | FlightDispatched -> true

let clear_action (state: RightClickState) = state.gesture <- Idle

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
    let configuredAction =
        if modifiers.shift && not modifiers.alt && not modifiers.control then
            navigation.routing.actions.shift_right_click
        elif modifiers.alt && not modifiers.shift && not modifiers.control then
            navigation.routing.actions.alt_right_click
        elif modifiers.control && not modifiers.shift && not modifiers.alt then
            navigation.routing.actions.ctrl_right_click
        else
            RoutedMouseAction.Off

    if RoutedMouseAction.enabled configuredAction then
        ValueSome configuredAction
    else
        ValueNone

let capabilities_allowed (navigation: State) (viewportName: string) =
    ViewportNameList.allows viewportName navigation.routing.actions.viewport_capabilities

let right_click_flight_entry_allowed (navigation: State) (viewportName: string) =
    capabilities_allowed navigation viewportName
    && ViewportNameList.allows viewportName navigation.routing.actions.right_click_flight_entry

let action
    (navigation: State)
    (viewport: RightClickViewport)
    (screenPoint: Point)
    (modifiers: MouseModifiers)
    (commandActive: bool)
    =
    let host = viewport.host
    let capabilitiesAllowed = capabilities_allowed navigation viewport.name

    let requestedAction =
        if capabilitiesAllowed then
            requested_gesture_action navigation modifiers
        else
            ValueNone

    match requestedAction with
    | ValueSome gestureAction ->
        ValueSome(
            NavigateView
                { host = host
                  action = gestureAction
                  screen_point = screenPoint }
        )
    | ValueNone when navigation_active navigation && navigation_exit_requested navigation -> ValueSome StopNavigation
    | ValueNone when navigation_active navigation -> ValueNone
    | ValueNone when
        capabilitiesAllowed
        && viewport.is_parallel
        && modifiers.shift
        && not modifiers.alt
        && not modifiers.control
        ->
        // Off still keeps Shift pan and Alt zoom in parallel views.
        ValueSome(PanParallel host)
    | ValueNone when
        capabilitiesAllowed
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
            || not commandActive)
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
    (tryView: nativeint -> RightClickViewport voption)
    (captureAllowsHost: ViewportHostIdentity -> bool)
    (commandActive: bool)
    (event: Win32.MouseHookEvent)
    =
    let isDown =
        event.message = Win32Native.WM_RBUTTONDOWN
        || event.message = Win32Native.WM_RBUTTONDBLCLK

    let isUp = event.message = Win32Native.WM_RBUTTONUP

    if isUp && state.gesture = NativeModifiedGesture then
        clear_action state
        false
    elif isDown && state.gesture = NativeModifiedGesture then
        false
    elif isUp && owns_button state then
        state.button_ownership <- NotOwned

        match state.gesture with
        | ButtonDown(EnterFlight entry) when entry_while_held entry.entry_mode -> clear_action state
        | ButtonDown(EnterFlight _ as captured) -> state.gesture <- ButtonReleased captured
        | ButtonDown captured -> state.gesture <- ButtonReleasedBeforeHandling captured
        | ButtonDownHandled captured -> state.gesture <- ButtonReleased captured
        | Idle
        | NativeModifiedGesture
        | ButtonReleasedBeforeHandling _
        | ButtonReleased _
        | FlightDispatched -> ()

        if not (try_wake navigation) then
            clear_action state

        true
    elif isDown && owns_button state then
        if state.button_ownership = ReleaseObserved then
            state.button_ownership <- NotOwned
            handle_event navigation state tryView captureAllowsHost commandActive event
        else
            true
    elif isDown && action_pending state then
        state.button_ownership <- Owned

        if not (try_wake navigation) then
            clear_action state

        true
    elif not isDown || navigation.lifecycle <> Available then
        false
    else
        let currentModifiers = event.modifiers

        match tryView event.hook_window with
        | ValueSome hookViewport ->
            match tryView event.point_window with
            | ValueSome pointViewport when
                MouseOverrideState.same_host hookViewport.host pointViewport.host
                && captureAllowsHost pointViewport.host
                ->
                match action navigation pointViewport event.screen_point currentModifiers commandActive with
                | ValueNone when
                    not (navigation_active navigation)
                    && (currentModifiers.shift || currentModifiers.alt || currentModifiers.control)
                    ->
                    state.gesture <- NativeModifiedGesture
                    false
                | ValueNone -> false
                | ValueSome captured ->
                    state.button_ownership <- Owned
                    state.gesture <- ButtonDown captured

                    if not (try_wake navigation) then
                        clear_action state

                    true
            | ValueSome _
            | ValueNone -> false
        | ValueNone -> false

let entry_command (entry: FlyEntry) =
    let flightMode = DefaultFlightMode.flight_mode entry.default_flight_mode

    if entry_while_held entry.entry_mode then
        match flightMode with
        | FlightMode.Temporary -> "'_RhinosCanFlyTempFlyHeld"
        | FlightMode.Normal
        | _ -> "'_RhinosCanFlyHeld"
    else
        match flightMode with
        | FlightMode.Temporary -> "'_RhinosCanFlyTempFly"
        | FlightMode.Normal
        | _ -> "'_RhinosCanFly"

let try_entry_view (entry: FlyEntry) =
    let view = RhinoView.FromRuntimeSerialNumber entry.host.view_serial_number
    let document = if isNull view then null else view.Document
    let activeDocument = RhinoDoc.ActiveDoc
    let (ViewWindowHandle expectedWindow) = entry.host.view_window

    if
        isNull view
        || isNull document
        || document.RuntimeSerialNumber <> entry.host.document_serial_number
        || isNull activeDocument
        || activeDocument.RuntimeSerialNumber <> entry.host.document_serial_number
        || view.Handle <> expectedWindow
        || MouseOverrideState.root_window view.Handle <> entry.host.root_window
    then
        ValueNone
    else
        ValueSome view

let try_prepare_entry_view (entry: FlyEntry) =
    let foregroundReady =
        MouseOverrideState.foreground_root_window () = entry.host.root_window
        || MouseOverrideState.try_bring_root_window_to_foreground entry.host.root_window

    if not foregroundReady then
        EntryUnavailable
    else
        match try_entry_view entry with
        | ValueNone -> EntryUnavailable
        | ValueSome view ->
            let activeView = view.Document.Views.ActiveView

            if isNull activeView || activeView.RuntimeSerialNumber <> view.RuntimeSerialNumber then
                view.Document.Views.ActiveView <- view
                EntryDeferred
            else
                EntryReady view

let dispatch_entry (state: RightClickState) (entry: FlyEntry) =
    state.gesture <- FlightDispatched

    if not (RhinoApp.RunScript(entry.host.document_serial_number, entry_command entry, false)) then
        Debug.WriteLine "RhinosCanFly right-click flight command was rejected by Rhino."

    if state.gesture = FlightDispatched then
        clear_action state

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

let try_dispatch_entry (navigation: State) (state: RightClickState) (commandActive: bool) (entry: FlyEntry) =
    if navigation.lifecycle <> Available then
        clear_action state
    else
        match try_prepare_entry_view entry with
        | EntryUnavailable -> clear_action state
        | EntryDeferred -> ()
        | EntryReady view ->
            if not (projection_allows_entry navigation view) then
                clear_action state
            else
                let canEnter = entry_during_commands entry.entry_mode || not commandActive

                if not canEnter then
                    clear_action state
                elif view.MouseCaptured false then
                    ()
                elif entry_while_held entry.entry_mode then
                    if Win32Native.GetAsyncKeyState Win32Native.VK_RBUTTON < 0s then
                        dispatch_entry state entry
                    else
                        clear_action state
                else
                    dispatch_entry state entry

let update (navigation: State) (state: RightClickState) (commandActive: bool) =
    match state.gesture with
    | Idle -> ()
    | NativeModifiedGesture -> ()
    | FlightDispatched ->
        if navigation.lifecycle <> Available then
            clear_action state
    | ButtonDown(NavigateView click) ->
        match apply_navigation_click navigation click with
        | GestureNavigationTransitions.Applied _ -> state.gesture <- ButtonDownHandled(NavigateView click)
        | GestureNavigationTransitions.Deferred -> ()
        | GestureNavigationTransitions.Failed error ->
            Debug.WriteLine $"RhinosCanFly mouse action: {error}"
            clear_action state
    | ButtonDown StopNavigation ->
        navigation.navigation_exit_requested <- true
        MouseOverrideState.keep_timer_running navigation
        state.gesture <- ButtonDownHandled StopNavigation
    | ButtonDown(PanParallel host) ->
        match GestureNavigationTransitions.prepare_action_view host with
        | GestureNavigationTransitions.ActionViewReady(_, activeHost) ->
            state.gesture <- ButtonDownHandled(PanParallel activeHost)
        | GestureNavigationTransitions.ActionViewDeferred -> ()
        | GestureNavigationTransitions.ActionViewUnavailable error ->
            Debug.WriteLine $"RhinosCanFly parallel pan: {error}"
            clear_action state
    | ButtonDown(ZoomParallel host) ->
        match GestureNavigationTransitions.prepare_action_view host with
        | GestureNavigationTransitions.ActionViewReady(_, activeHost) ->
            state.gesture <- ButtonDownHandled(ZoomParallel activeHost)
        | GestureNavigationTransitions.ActionViewDeferred -> ()
        | GestureNavigationTransitions.ActionViewUnavailable error ->
            Debug.WriteLine $"RhinosCanFly parallel zoom: {error}"
            clear_action state
    | ButtonDownHandled _ -> ()
    | ButtonReleasedBeforeHandling(NavigateView click) ->
        match apply_navigation_click navigation click with
        | GestureNavigationTransitions.Applied _ ->
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
    | ButtonReleasedBeforeHandling(EnterFlight entry) -> try_dispatch_entry navigation state commandActive entry
    | ButtonDown(EnterFlight entry) when entry_while_held entry.entry_mode ->
        try_dispatch_entry navigation state commandActive entry
    | ButtonDown(EnterFlight _) -> ()
    | ButtonReleased(NavigateView _) ->
        GestureNavigationTransitions.release navigation GestureOwner.ModifiedRightClick
        clear_action state
    | ButtonReleased StopNavigation -> clear_action state
    | ButtonReleased(PanParallel _) -> clear_action state
    | ButtonReleased(ZoomParallel _) -> clear_action state
    | ButtonReleased(EnterFlight entry) -> try_dispatch_entry navigation state commandActive entry

let command_began (state: RightClickState) =
    match state.gesture with
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

let prune_released_button (state: RightClickState) =
    if owns_button state then
        if Win32Native.GetAsyncKeyState Win32Native.VK_RBUTTON < 0s then
            state.button_ownership <- Owned
        elif state.button_ownership = ReleaseObserved then
            state.button_ownership <- NotOwned
            clear_action state
        else
            state.button_ownership <- ReleaseObserved

let reconcile_physical_button (state: RightClickState) =
    if owns_button state then
        if Win32Native.GetAsyncKeyState Win32Native.VK_RBUTTON < 0s then
            state.button_ownership <- Owned
        else
            state.button_ownership <- NotOwned
            clear_action state

let parallel_zoom_host (state: RightClickState) =
    match state.gesture with
    | ButtonDownHandled(ZoomParallel host) ->
        match state.button_ownership with
        | Owned
        | ReleaseObserved -> ValueSome host
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
        | Owned
        | ReleaseObserved -> ValueSome host
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
    state.gesture <- Idle
    state.button_ownership <- NotOwned
