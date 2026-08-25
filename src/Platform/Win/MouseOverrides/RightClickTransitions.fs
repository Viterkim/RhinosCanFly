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
      default_flight_mode: DefaultFlightMode
      started_at: int64 }

type NavigationClick =
    { host: ViewportHostIdentity
      action: RoutedMouseAction
      screen_point: Point
      started_at: int64 }

type DirectParallelClick =
    { host: ViewportHostIdentity
      started_at: int64 }

type RightClickAction =
    | EnterFlight of FlyEntry
    | NavigateView of NavigationClick
    | StopNavigation
    | PanParallel of DirectParallelClick
    | ZoomParallel of DirectParallelClick

type RightClickGesture =
    | Idle
    | NativeModifiedGesture
    | ButtonDown of RightClickAction
    | ButtonDownHandled of RightClickAction
    | ButtonReleasedBeforeHandling of RightClickAction
    | ButtonReleased of RightClickAction
    | FlightDispatched of FlyEntry

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

[<Literal>]
let ENTRY_TIMEOUT_SECONDS = 2.

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
    | FlightDispatched _ -> true

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
                  screen_point = screenPoint
                  started_at = Stopwatch.GetTimestamp() }
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
        ValueSome(
            PanParallel
                { host = host
                  started_at = Stopwatch.GetTimestamp() }
        )
    | ValueNone when
        capabilitiesAllowed
        && viewport.is_parallel
        && modifiers.alt
        && not modifiers.shift
        && not modifiers.control
        ->
        ValueSome(
            ZoomParallel
                { host = host
                  started_at = Stopwatch.GetTimestamp() }
        )
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
                  default_flight_mode = navigation.routing.actions.default_flight_mode
                  started_at = Stopwatch.GetTimestamp() }
        )
    | ValueNone -> ValueNone

let rec handle_event
    (navigation: State)
    (state: RightClickState)
    (tryView: nativeint -> RightClickViewport voption)
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
        | FlightDispatched _ -> ()

        if not (try_wake navigation) then
            clear_action state

        true
    elif isDown && owns_button state then
        if state.button_ownership = ReleaseObserved then
            state.button_ownership <- NotOwned
            handle_event navigation state tryView commandActive event
        else
            true
    elif isDown && action_pending state then
        state.button_ownership <- Owned

        if not (try_wake navigation) then
            clear_action state

        true
    elif
        not isDown
        || navigation.lifecycle <> Available
        || Win32Native.GetCapture() <> nativeint 0
    then
        false
    else
        let currentModifiers = event.modifiers

        match tryView event.point_window with
        | ValueSome pointViewport when pointViewport.host.root_window = MouseOverrideState.foreground_root_window () ->
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

let entry_timed_out (entry: FlyEntry) =
    let elapsedTicks = Stopwatch.GetTimestamp() - entry.started_at
    float elapsedTicks / float Stopwatch.Frequency >= ENTRY_TIMEOUT_SECONDS

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
    if MouseOverrideState.foreground_root_window () <> entry.host.root_window then
        if MouseOverrideState.try_bring_root_window_to_foreground entry.host.root_window then
            EntryDeferred
        else
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
    state.gesture <- FlightDispatched entry

    if not (RhinoApp.RunScript(entry.host.document_serial_number, entry_command entry, false)) then
        Debug.WriteLine "RhinosCanFly right-click flight command was rejected by Rhino."
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

let action_timed_out (startedAt: int64) =
    let elapsedTicks = Stopwatch.GetTimestamp() - startedAt
    float elapsedTicks / float Stopwatch.Frequency >= ENTRY_TIMEOUT_SECONDS

let navigation_click_timed_out (click: NavigationClick) = action_timed_out click.started_at

let update (navigation: State) (state: RightClickState) (commandActive: bool) =
    match state.gesture with
    | Idle -> ()
    | NativeModifiedGesture -> ()
    | FlightDispatched entry ->
        if navigation.lifecycle <> Available || entry_timed_out entry then
            clear_action state
    | ButtonDown(NavigateView click) ->
        if navigation_click_timed_out click then
            clear_action state
        else
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
    | ButtonDown(PanParallel click) ->
        if action_timed_out click.started_at then
            clear_action state
        else
            match GestureNavigationTransitions.prepare_action_view click.host with
            | GestureNavigationTransitions.ActionViewReady(_, activeHost) ->
                state.gesture <-
                    ButtonDownHandled(
                        PanParallel
                            { host = activeHost
                              started_at = click.started_at }
                    )
            | GestureNavigationTransitions.ActionViewDeferred -> ()
            | GestureNavigationTransitions.ActionViewUnavailable error ->
                Debug.WriteLine $"RhinosCanFly parallel pan: {error}"
                clear_action state
    | ButtonDown(ZoomParallel click) ->
        if action_timed_out click.started_at then
            clear_action state
        else
            match GestureNavigationTransitions.prepare_action_view click.host with
            | GestureNavigationTransitions.ActionViewReady(_, activeHost) ->
                state.gesture <-
                    ButtonDownHandled(
                        ZoomParallel
                            { host = activeHost
                              started_at = click.started_at }
                    )
            | GestureNavigationTransitions.ActionViewDeferred -> ()
            | GestureNavigationTransitions.ActionViewUnavailable error ->
                Debug.WriteLine $"RhinosCanFly parallel zoom: {error}"
                clear_action state
    | ButtonDownHandled _ -> ()
    | ButtonReleasedBeforeHandling(NavigateView click) ->
        if navigation_click_timed_out click then
            clear_action state
        else
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
    | ButtonReleasedBeforeHandling(ZoomParallel _)
    | ButtonReleasedBeforeHandling(EnterFlight _) -> clear_action state
    | ButtonDown(EnterFlight entry) ->
        if navigation.lifecycle <> Available || entry_timed_out entry then
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

                    let heldAndDown =
                        entry_while_held entry.entry_mode
                        && Win32Native.GetAsyncKeyState Win32Native.VK_RBUTTON < 0s

                    if heldAndDown && canEnter && not (view.MouseCaptured false) then
                        dispatch_entry state entry
                    elif entry_while_held entry.entry_mode && not heldAndDown then
                        clear_action state
    | ButtonReleased(NavigateView _) ->
        GestureNavigationTransitions.release navigation GestureOwner.ModifiedRightClick
        clear_action state
    | ButtonReleased StopNavigation -> clear_action state
    | ButtonReleased(PanParallel _) -> clear_action state
    | ButtonReleased(ZoomParallel _) -> clear_action state
    | ButtonReleased(EnterFlight entry) ->
        if navigation.lifecycle <> Available || entry_timed_out entry then
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

                    if canEnter && not (view.MouseCaptured false) then
                        dispatch_entry state entry

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
    | ButtonDownHandled(ZoomParallel click) ->
        match state.button_ownership with
        | Owned
        | ReleaseObserved -> ValueSome click.host
        | NotOwned -> ValueNone
    | Idle
    | NativeModifiedGesture
    | ButtonDown _
    | ButtonDownHandled _
    | ButtonReleasedBeforeHandling _
    | ButtonReleased _
    | FlightDispatched _ -> ValueNone

let parallel_pan_host (state: RightClickState) =
    match state.gesture with
    | ButtonDownHandled(PanParallel click) ->
        match state.button_ownership with
        | Owned
        | ReleaseObserved -> ValueSome click.host
        | NotOwned -> ValueNone
    | Idle
    | NativeModifiedGesture
    | ButtonDown _
    | ButtonDownHandled _
    | ButtonReleasedBeforeHandling _
    | ButtonReleased _
    | FlightDispatched _ -> ValueNone

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
    | FlightDispatched _ -> ()

let reset (state: RightClickState) =
    state.gesture <- Idle
    state.button_ownership <- NotOwned
