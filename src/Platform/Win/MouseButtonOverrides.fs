module RhinosCanFly.Platform.Win.MouseButtonOverrides

open System
open System.Diagnostics
open System.Drawing
open System.Windows.Forms
open RhinosCanFly
open Rhino.UI

type RoutingConfig =
    { enabled: bool
      mouse4: bool
      mouse5: bool }

type SideButton =
    | Mouse4
    | Mouse5

type ButtonState =
    { mutable pending: Point option
      mutable routed: bool }

type State =
    { mutable routing: RoutingConfig
      mutable suspended: bool
      mutable shut_down: bool
      mouse4: ButtonState
      mouse5: ButtonState }

let dragSize = SystemInformation.DragSize
let releaseTimer = new Timer(Interval = 15)

let state =
    { routing =
        { enabled = false
          mouse4 = false
          mouse5 = false }
      suspended = false
      shut_down = false
      mouse4 = { pending = None; routed = false }
      mouse5 = { pending = None; routed = false } }

let button_state (button: SideButton) =
    match button with
    | Mouse4 -> state.mouse4
    | Mouse5 -> state.mouse5

let key (button: SideButton) =
    match button with
    | Mouse4 -> Keys.XButton1
    | Mouse5 -> Keys.XButton2

let enabled_for (button: SideButton) =
    state.routing.enabled
    && match button with
       | Mouse4 -> state.routing.mouse4
       | Mouse5 -> state.routing.mouse5

let is_down (button: SideButton) =
    Win32.GetAsyncKeyState(int (key button)) < 0s

let any_routed () =
    state.mouse4.routed || state.mouse5.routed

let any_pending () =
    Option.isSome state.mouse4.pending || Option.isSome state.mouse5.pending

let moved_enough (start: Point) (current: Point) =
    abs (current.X - start.X) >= max 1 (dragSize.Width / 2)
    || abs (current.Y - start.Y) >= max 1 (dragSize.Height / 2)

let keep_timer_running () =
    if not releaseTimer.Enabled then
        releaseTimer.Start()

let stop_timer_if_idle () =
    if not (any_pending ()) && not (any_routed ()) then
        releaseTimer.Stop()

let begin_route (button: SideButton) =
    let buttonState = button_state button

    if any_routed () then
        buttonState.pending <- None
        buttonState.routed <- true
    else
        match Win32.send_middle_mouse true with
        | Ok() ->
            buttonState.pending <- None
            buttonState.routed <- true
        | Error error -> Debug.WriteLine $"RhinosCanFly mouse override: {error}"

let finish_button (button: SideButton) =
    let buttonState = button_state button
    buttonState.pending <- None

    if buttonState.routed then
        buttonState.routed <- false

        if not (any_routed ()) then
            match Win32.send_middle_mouse false with
            | Ok() -> ()
            | Error error ->
                buttonState.routed <- true
                Debug.WriteLine $"RhinosCanFly mouse override: {error}"

    stop_timer_if_idle ()

let release_all () =
    state.mouse4.pending <- None
    state.mouse5.pending <- None

    if not (any_routed ()) then
        releaseTimer.Stop()
        Ok()
    else
        match Win32.send_middle_mouse false with
        | Ok() ->
            state.mouse4.routed <- false
            state.mouse5.routed <- false
            releaseTimer.Stop()
            Ok()
        | Error error -> Error error

let update_button (button: SideButton) (current: Point) =
    let buttonState = button_state button

    if not (enabled_for button) || not (is_down button) then
        if Option.isSome buttonState.pending || buttonState.routed then
            finish_button button
    elif buttonState.routed then
        ()
    else
        match buttonState.pending with
        | None ->
            buttonState.pending <- Some current
            keep_timer_running ()
        | Some start when moved_enough start current -> begin_route button
        | Some _ -> ()

let update_from_viewport_move () =
    let current = Control.MousePosition
    update_button Mouse4 current
    update_button Mouse5 current

type SideButtonCallback() =
    inherit MouseCallback()

    override _.OnMouseMove(_event: MouseCallbackEventArgs) =
        try
            update_from_viewport_move ()
        with error ->
            Debug.WriteLine $"RhinosCanFly mouse override callback: {error.Message}"

let callback = SideButtonCallback()

releaseTimer.Tick.Add(fun (_: EventArgs) ->
    try
        if not (is_down Mouse4) then
            finish_button Mouse4

        if not (is_down Mouse5) then
            finish_button Mouse5
    with error ->
        Debug.WriteLine $"RhinosCanFly mouse override timer: {error.Message}")

let apply (source: FlyConfigFile) =
    if state.shut_down then
        Error "Mouse button overrides have already shut down."
    else
        match release_all () with
        | Error error -> Error error
        | Ok() ->
            state.routing <-
                { enabled = source.mouse_button_overrides_enabled
                  mouse4 = source.mouse4_acts_as_middle
                  mouse5 = source.mouse5_acts_as_middle }

            callback.Enabled <-
                not state.suspended
                && state.routing.enabled
                && (state.routing.mouse4 || state.routing.mouse5)

            Ok()

let suspend () =
    if not state.shut_down then
        state.suspended <- true
        callback.Enabled <- false

        match release_all () with
        | Ok() -> ()
        | Error error -> Debug.WriteLine $"RhinosCanFly mouse override suspend: {error}"

let resume () =
    if not state.shut_down then
        state.suspended <- false

        callback.Enabled <- state.routing.enabled && (state.routing.mouse4 || state.routing.mouse5)

let shutdown () =
    if not state.shut_down then
        state.shut_down <- true
        state.suspended <- true
        callback.Enabled <- false

        match release_all () with
        | Ok() -> ()
        | Error error -> Debug.WriteLine $"RhinosCanFly mouse override shutdown: {error}"

        releaseTimer.Dispose()
