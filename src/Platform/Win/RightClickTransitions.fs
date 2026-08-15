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

type RightClickGesture =
    | Idle
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
      is_perspective: bool }

[<Struct>]
type Modifiers = { shift: bool; alt: bool }

[<Literal>]
let entry_timeout_seconds = 2.

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
    Option.isSome navigation.routing.shift_right_click
    || Option.isSome navigation.routing.alt_right_click

let navigation_active (navigation: State) =
    ViewNavigationState.any_button_engaged navigation
    || ViewNavigationState.view_latch_engaged navigation

let navigation_exit_enabled (navigation: State) =
    navigation_active navigation
    && ViewNavigationState.right_mouse_exit_enabled navigation

let capture_needed (navigation: State) (state: RightClickState) =
    entry_enabled navigation
    || modified_navigation_enabled navigation
    || navigation_exit_enabled navigation
    || owns_button state
    || action_pending state

let modifiers () =
    { shift = ViewNavigationState.shift_down ()
      alt = ViewNavigationState.alt_down () }

let requested_navigation_mode (navigation: State) (modifiers: Modifiers) =
    let shiftMode =
        if modifiers.shift then
            navigation.routing.shift_right_click
        else
            None

    let altMode =
        if modifiers.alt then
            navigation.routing.alt_right_click
        else
            None

    if shiftMode = Some Pan || altMode = Some Pan then
        ValueSome Pan
    elif shiftMode = Some Pivot || altMode = Some Pivot then
        ValueSome Pivot
    else
        ValueNone

let action (navigation: State) (viewport: RightClickViewport) (modifiers: Modifiers) (commandActive: bool) =
    let host = viewport.host

    if navigation_active navigation then
        if navigation_exit_enabled navigation then
            ValueSome(
                NavigateView
                    { host = host
                      request = StopNavigation }
            )
        else
            ValueNone
    else
        match requested_navigation_mode navigation modifiers with
        | ValueSome mode ->
            ValueSome(
                NavigateView
                    { host = host
                      request = StartNavigation mode }
            )
        | ValueNone when
            entry_enabled navigation
            && viewport.is_perspective
            && (entry_during_commands navigation.routing.right_click_entry || not commandActive)
            && not modifiers.shift
            && not modifiers.alt
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

    if isUp && owns_button state then
        state.button_ownership <- NotOwned

        match state.gesture with
        | ButtonDown(EnterFlight entry) when entry_while_held entry.entry_mode -> clear_action state
        | ButtonDown captured -> state.gesture <- ButtonReleased captured
        | Idle
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
        match tryView event.hook_window with
        | ValueNone -> false
        | ValueSome hookViewport ->
            match tryView event.point_window with
            | ValueSome pointViewport when ViewNavigationState.same_host hookViewport.host pointViewport.host ->
                match action navigation pointViewport (modifiers ()) commandActive with
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
    float elapsedTicks / float Stopwatch.Frequency >= entry_timeout_seconds

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
    | FlightDispatched entry ->
        if navigation.lifecycle <> Available || entry_timed_out entry then
            clear_action state
    | ButtonDown(NavigateView _) -> ()
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

let reset (state: RightClickState) =
    state.gesture <- Idle
    state.button_ownership <- NotOwned
