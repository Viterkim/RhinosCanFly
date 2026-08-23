module RhinosCanFly.Platform.Win.RightClickTransitions

open System.Diagnostics
open Rhino
open Rhino.Display
open RhinosCanFly
open RhinosCanFly.Platform.Win.ViewNavigationTypes

type FlyEntry =
    { host: ViewportHostIdentity
      entry_mode: RightClickEntryMode
      default_flight_mode: DefaultFlightMode
      started_at: int64 }

type NavigationClick =
    { host: ViewportHostIdentity
      request: ViewNavigationRequest }

type RightClickAction =
    | EnterFlight of FlyEntry
    | NavigateView of NavigationClick
    | PanParallel of ViewportHostIdentity
    | ZoomParallel of ViewportHostIdentity

type RightClickGesture =
    | Idle
    | NativeModifiedGesture
    | ButtonDown of RightClickAction
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

[<Struct>]
type Modifiers =
    { shift: bool
      alt: bool
      control: bool }

[<Literal>]
let ENTRY_TIMEOUT_SECONDS = 2.

let create () =
    { gesture = Idle
      button_ownership = NotOwned }

let try_wake (navigation: State) =
    try
        ViewNavigationState.keep_timer_running navigation
        true
    with error ->
        Debug.WriteLine $"RhinosCanFly right-click timer: {error}"
        false

let entry_enabled (navigation: State) =
    match navigation.routing.right_click_entry with
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
    | ButtonDown _
    | ButtonReleased _
    | FlightDispatched _ -> true

let clear_action (state: RightClickState) = state.gesture <- Idle

let owns_button (state: RightClickState) =
    match state.button_ownership with
    | NotOwned -> false
    | Owned
    | ReleaseObserved -> true

let modified_navigation_enabled (navigation: State) =
    navigation.routing.shift_right_click <> MouseGestureAction.Off
    || navigation.routing.alt_right_click <> MouseGestureAction.Off
    || navigation.routing.ctrl_right_click <> MouseGestureAction.Off

let navigation_active (navigation: State) =
    ViewNavigationState.any_button_engaged navigation
    || ViewNavigationState.view_latch_engaged navigation

let navigation_exit_requested (navigation: State) =
    navigation_active navigation
    && ViewNavigationState.right_mouse_exit_requested navigation

let navigation_exit_capture_needed (navigation: State) =
    navigation_active navigation
    && ViewNavigationState.right_mouse_exit_capture_needed navigation

let capture_needed (navigation: State) (state: RightClickState) =
    navigation.routing.runtime_enabled
    || entry_enabled navigation
    || modified_navigation_enabled navigation
    || navigation_exit_capture_needed navigation
    || owns_button state
    || action_pending state

let modifiers () =
    { shift = ViewNavigationState.shift_down ()
      alt = ViewNavigationState.alt_down ()
      control = ViewNavigationState.control_down () }

let requested_gesture_action (navigation: State) (modifiers: Modifiers) =
    let configuredAction =
        if modifiers.shift && not modifiers.alt && not modifiers.control then
            navigation.routing.shift_right_click
        elif modifiers.alt && not modifiers.shift && not modifiers.control then
            navigation.routing.alt_right_click
        elif modifiers.control && not modifiers.shift && not modifiers.alt then
            navigation.routing.ctrl_right_click
        else
            MouseGestureAction.Off

    if configuredAction = MouseGestureAction.Off then
        ValueNone
    else
        ValueSome configuredAction

let action (navigation: State) (viewport: RightClickViewport) (modifiers: Modifiers) (commandActive: bool) =
    let host = viewport.host

    if navigation_active navigation then
        if navigation_exit_requested navigation then
            ValueSome(
                NavigateView
                    { host = host
                      request = StopNavigation }
            )
        else
            ValueNone
    else
        match requested_gesture_action navigation modifiers with
        | ValueSome gestureAction ->
            match ViewLatchTransitions.configured_mode gestureAction with
            | Some mode ->
                ValueSome(
                    NavigateView
                        { host = host
                          request = StartNavigation mode }
                )
            | None -> ValueNone
        | ValueNone when
            navigation.routing.runtime_enabled
            && viewport.is_parallel
            && modifiers.shift
            && not modifiers.alt
            && not modifiers.control
            ->
            ValueSome(PanParallel host)
        | ValueNone when
            navigation.routing.runtime_enabled
            && viewport.is_parallel
            && modifiers.alt
            && not modifiers.shift
            && not modifiers.control
            ->
            ValueSome(ZoomParallel host)
        | ValueNone when
            entry_enabled navigation
            && (viewport.is_perspective
                || (viewport.is_parallel
                    && ParallelViewFlying.allows viewport.name navigation.routing.parallel_view_flying))
            && (entry_during_commands navigation.routing.right_click_entry || not commandActive)
            && not modifiers.shift
            && not modifiers.alt
            && not modifiers.control
            ->
            ValueSome(
                EnterFlight
                    { host = host
                      entry_mode = navigation.routing.right_click_entry
                      default_flight_mode = navigation.routing.default_flight_mode
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
        | ButtonDown captured -> state.gesture <- ButtonReleased captured
        | Idle
        | NativeModifiedGesture
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
    elif not isDown || navigation.lifecycle <> Available then
        false
    else
        let currentModifiers = modifiers ()

        match tryView event.hook_window with
        | ValueNone -> false
        | ValueSome hookViewport ->
            match tryView event.point_window with
            | ValueSome pointViewport when ViewNavigationState.same_host hookViewport.host pointViewport.host ->
                match action navigation pointViewport currentModifiers commandActive with
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
        || ViewNavigationState.root_window view.Handle <> entry.host.root_window
    then
        ValueNone
    else
        ValueSome view

let try_prepare_entry_view (entry: FlyEntry) =
    if ViewNavigationState.foreground_root_window () <> entry.host.root_window then
        if ViewNavigationState.try_bring_root_window_to_foreground entry.host.root_window then
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

    RhinoApp.RunScript(entry.host.document_serial_number, entry_command entry, false)
    |> ignore

let update (navigation: State) (state: RightClickState) (commandActive: bool) =
    match state.gesture with
    | Idle -> ()
    | NativeModifiedGesture -> ()
    | FlightDispatched entry ->
        if navigation.lifecycle <> Available || entry_timed_out entry then
            clear_action state
    | ButtonDown(NavigateView _) -> ()
    | ButtonDown(PanParallel _) -> ()
    | ButtonDown(ZoomParallel _) -> ()
    | ButtonDown(EnterFlight entry) ->
        if navigation.lifecycle <> Available || entry_timed_out entry then
            clear_action state
        else
            match try_prepare_entry_view entry with
            | EntryUnavailable -> clear_action state
            | EntryDeferred -> ()
            | EntryReady view ->
                let canEnter = entry_during_commands entry.entry_mode || not commandActive

                let heldAndDown =
                    entry_while_held entry.entry_mode
                    && Win32Native.GetAsyncKeyState Win32Native.VK_RBUTTON < 0s

                if heldAndDown && canEnter && not (view.MouseCaptured false) then
                    dispatch_entry state entry
                elif entry_while_held entry.entry_mode && not heldAndDown then
                    clear_action state
    | ButtonReleased(NavigateView click) ->
        if
            navigation.lifecycle = Available
            && ViewNavigationState.try_bring_root_window_to_foreground click.host.root_window
        then
            match ViewLatchTransitions.apply_right_click_request navigation click.host click.request with
            | Ok() -> ()
            | Error error -> Debug.WriteLine $"RhinosCanFly right-click navigation: {error}"

        clear_action state
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

let parallel_zoom_host (state: RightClickState) =
    match state.gesture with
    | ButtonDown(ZoomParallel host) ->
        match state.button_ownership with
        | Owned
        | ReleaseObserved -> ValueSome host
        | NotOwned -> ValueNone
    | Idle
    | NativeModifiedGesture
    | ButtonDown _
    | ButtonReleased _
    | FlightDispatched _ -> ValueNone

let parallel_pan_host (state: RightClickState) =
    match state.gesture with
    | ButtonDown(PanParallel host) ->
        match state.button_ownership with
        | Owned
        | ReleaseObserved -> ValueSome host
        | NotOwned -> ValueNone
    | Idle
    | NativeModifiedGesture
    | ButtonDown _
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
    | ButtonReleased(PanParallel _)
    | ButtonReleased(ZoomParallel _) -> clear_action state
    | Idle
    | NativeModifiedGesture
    | ButtonDown _
    | ButtonReleased _
    | FlightDispatched _ -> ()

let reset (state: RightClickState) =
    state.gesture <- Idle
    state.button_ownership <- NotOwned
