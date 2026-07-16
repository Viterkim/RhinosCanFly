module RhinosCanFly.MouseButtonOverrides

open System
open System.Diagnostics
open System.Drawing
open System.Windows.Forms
open Rhino.UI

type RoutingConfig =
    { enabled: bool
      mouse4: bool
      mouse5: bool }

type SideButton =
    | Mouse4
    | Mouse5

let marker = unativeint 0x5243464Du
let dragSize = SystemInformation.DragSize
let releaseTimer = new Timer(Interval = 15)

let mutable routing =
    { enabled = false
      mouse4 = false
      mouse5 = false }

let mutable suspended = false
let mutable mouse4Pending: Point option = None
let mutable mouse5Pending: Point option = None
let mutable mouse4Routed = false
let mutable mouse5Routed = false

let key (button: SideButton) =
    match button with
    | Mouse4 -> Keys.XButton1
    | Mouse5 -> Keys.XButton2

let enabled_for (button: SideButton) =
    routing.enabled
    && match button with
       | Mouse4 -> routing.mouse4
       | Mouse5 -> routing.mouse5

let is_down (button: SideButton) =
    Win32.GetAsyncKeyState(int (key button)) < 0s

let pending (button: SideButton) =
    match button with
    | Mouse4 -> mouse4Pending
    | Mouse5 -> mouse5Pending

let set_pending (button: SideButton) (value: Point option) =
    match button with
    | Mouse4 -> mouse4Pending <- value
    | Mouse5 -> mouse5Pending <- value

let routed (button: SideButton) =
    match button with
    | Mouse4 -> mouse4Routed
    | Mouse5 -> mouse5Routed

let set_routed (button: SideButton) (value: bool) =
    match button with
    | Mouse4 -> mouse4Routed <- value
    | Mouse5 -> mouse5Routed <- value

let any_routed () = mouse4Routed || mouse5Routed

let any_pending () =
    Option.isSome mouse4Pending || Option.isSome mouse5Pending

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
    if any_routed () then
        set_pending button None
        set_routed button true
    else
        match Win32.send_middle_mouse true marker with
        | Ok() ->
            set_pending button None
            set_routed button true
        | Error error -> Debug.WriteLine $"RhinosCanFly mouse override: {error}"

let finish_button (button: SideButton) =
    set_pending button None

    if routed button then
        set_routed button false

        if not (any_routed ()) then
            match Win32.send_middle_mouse false marker with
            | Ok() -> ()
            | Error error ->
                set_routed button true
                Debug.WriteLine $"RhinosCanFly mouse override: {error}"

    stop_timer_if_idle ()

let release_all () =
    mouse4Pending <- None
    mouse5Pending <- None

    if not (any_routed ()) then
        releaseTimer.Stop()
        Ok()
    else
        match Win32.send_middle_mouse false marker with
        | Ok() ->
            mouse4Routed <- false
            mouse5Routed <- false
            releaseTimer.Stop()
            Ok()
        | Error error -> Error error

let update_button (button: SideButton) (current: Point) =
    if not (enabled_for button) || not (is_down button) then
        if Option.isSome (pending button) || routed button then
            finish_button button
    elif routed button then
        ()
    else
        match pending button with
        | None ->
            set_pending button (Some current)
            keep_timer_running ()
        | Some start when moved_enough start current -> begin_route button
        | Some _ -> ()

let update_from_viewport_move () =
    let current = Control.MousePosition
    update_button Mouse4 current
    update_button Mouse5 current

type SideButtonCallback() =
    inherit MouseCallback()

    override _.OnMouseMove(_event: MouseCallbackEventArgs) = update_from_viewport_move ()

let callback = SideButtonCallback()

releaseTimer.Tick.Add(fun (_: EventArgs) ->
    if not (is_down Mouse4) then
        finish_button Mouse4

    if not (is_down Mouse5) then
        finish_button Mouse5)

let apply (source: FlyConfigFile) =
    match release_all () with
    | Error error -> Error error
    | Ok() ->
        routing <-
            { enabled = source.mouse_button_overrides_enabled
              mouse4 = source.mouse4_acts_as_middle
              mouse5 = source.mouse5_acts_as_middle }

        callback.Enabled <- not suspended && routing.enabled && (routing.mouse4 || routing.mouse5)
        Ok()

let suspend () =
    suspended <- true
    callback.Enabled <- false

    match release_all () with
    | Ok() -> ()
    | Error error -> Debug.WriteLine $"RhinosCanFly mouse override suspend: {error}"

let resume () =
    suspended <- false
    callback.Enabled <- routing.enabled && (routing.mouse4 || routing.mouse5)

let initialize_from_config () =
    match Config.load () with
    | Ok loaded -> apply loaded.config_file
    | Error error -> Error error

let shutdown () =
    suspended <- true
    callback.Enabled <- false

    match release_all () with
    | Ok() -> ()
    | Error error -> Debug.WriteLine $"RhinosCanFly mouse override shutdown: {error}"

    releaseTimer.Dispose()
