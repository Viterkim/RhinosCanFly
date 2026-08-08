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
      mouse5: bool
      shift_right_click: bool
      alt_right_click: bool
      shift_right_click_pan: bool
      alt_right_click_pan: bool }

type SideButton =
    | Mouse4
    | Mouse5

type ButtonState =
    { mutable pending: Point option
      mutable routed: bool }

type ViewLatchMode =
    | Pivot
    | Pan

type ViewLatch =
    | Idle
    | WaitingForRelease of nativeint * ViewLatchMode
    | Restarting of nativeint * bool * ViewLatchMode
    | Active of nativeint * bool * ViewLatchMode

type State =
    { mutable routing: RoutingConfig
      mutable suspended: bool
      mutable shut_down: bool
      mouse4: ButtonState
      mouse5: ButtonState
      mutable view_latch: ViewLatch
      mutable synthetic_shift_down: bool
      mutable routed_modifiers_down: bool }

let dragSize = SystemInformation.DragSize
let releaseTimer = new Timer(Interval = 15)

let state =
    { routing =
        { enabled = false
          mouse4 = false
          mouse5 = false
          shift_right_click = false
          alt_right_click = false
          shift_right_click_pan = false
          alt_right_click_pan = false }
      suspended = false
      shut_down = false
      mouse4 = { pending = None; routed = false }
      mouse5 = { pending = None; routed = false }
      view_latch = Idle
      synthetic_shift_down = false
      routed_modifiers_down = false }

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

let view_latch_active () =
    match state.view_latch with
    | Active _ -> true
    | Idle
    | WaitingForRelease _
    | Restarting _ -> false

let view_latch_engaged () =
    match state.view_latch with
    | Idle -> false
    | WaitingForRelease _
    | Restarting _
    | Active _ -> true

let middle_mouse_down () = any_routed () || view_latch_active ()

let release_synthetic_shift () =
    if not state.synthetic_shift_down then
        Ok()
    else
        match Win32.send_shift_key false with
        | Ok() ->
            state.synthetic_shift_down <- false
            Ok()
        | Error error -> Error error

let shift_down () =
    Win32.GetAsyncKeyState Win32.VK_SHIFT < 0s
    || Win32.GetAsyncKeyState Win32.VK_LSHIFT < 0s
    || Win32.GetAsyncKeyState Win32.VK_RSHIFT < 0s

let alt_down () =
    Win32.GetAsyncKeyState Win32.VK_MENU < 0s
    || Win32.GetAsyncKeyState Win32.VK_LMENU < 0s
    || Win32.GetAsyncKeyState Win32.VK_RMENU < 0s

let view_modifier_down () = shift_down () || alt_down ()

let moved_enough (start: Point) (current: Point) =
    abs (current.X - start.X) >= max 1 (dragSize.Width / 2)
    || abs (current.Y - start.Y) >= max 1 (dragSize.Height / 2)

let keep_timer_running () =
    if not releaseTimer.Enabled then
        releaseTimer.Start()

let stop_timer_if_idle () =
    if not (any_pending ()) && not (any_routed ()) && not (view_latch_engaged ()) then
        releaseTimer.Stop()

let begin_route (button: SideButton) =
    let buttonState = button_state button

    if middle_mouse_down () then
        buttonState.pending <- None
        buttonState.routed <- true
    else
        match Win32.send_middle_mouse true with
        | Ok() ->
            buttonState.pending <- None
            buttonState.routed <- true
            state.routed_modifiers_down <- view_modifier_down ()
        | Error error -> Debug.WriteLine $"RhinosCanFly mouse override: {error}"

let finish_button (button: SideButton) =
    let buttonState = button_state button
    buttonState.pending <- None

    if buttonState.routed then
        buttonState.routed <- false

        if not (middle_mouse_down ()) then
            match Win32.send_middle_mouse false with
            | Ok() ->
                state.routed_modifiers_down <- false

                match release_synthetic_shift () with
                | Ok() -> ()
                | Error error -> Debug.WriteLine $"RhinosCanFly mouse override: {error}"
            | Error error ->
                buttonState.routed <- true
                Debug.WriteLine $"RhinosCanFly mouse override: {error}"

    stop_timer_if_idle ()

let release_all () =
    state.mouse4.pending <- None
    state.mouse5.pending <- None

    if not (middle_mouse_down ()) then
        state.view_latch <- Idle
        releaseTimer.Stop()
        release_synthetic_shift ()
    else
        match Win32.send_middle_mouse false with
        | Error error -> Error error
        | Ok() ->
            state.mouse4.routed <- false
            state.mouse5.routed <- false
            state.view_latch <- Idle
            state.routed_modifiers_down <- false
            releaseTimer.Stop()
            release_synthetic_shift ()

let root_window (window: nativeint) =
    let root = Win32.GetAncestor(window, Win32.GA_ROOT)

    if root = nativeint 0 then window else root

let release_view_latch () =
    match state.view_latch with
    | Idle -> Ok()
    | WaitingForRelease _ ->
        state.view_latch <- Idle
        stop_timer_if_idle ()
        Ok()
    | Restarting _ ->
        state.view_latch <- Idle
        stop_timer_if_idle ()
        Ok()
    | Active(window, modifiersDown, mode) ->
        state.view_latch <- Idle

        if any_routed () then
            stop_timer_if_idle ()
            release_synthetic_shift ()
        else
            match Win32.send_middle_mouse false with
            | Ok() ->
                stop_timer_if_idle ()
                release_synthetic_shift ()
            | Error error ->
                state.view_latch <- Active(window, modifiersDown, mode)
                Error error

let handle_right_click (window: nativeint) =
    match state.view_latch with
    | Active _
    | WaitingForRelease _
    | Restarting _ ->
        match release_view_latch () with
        | Ok() -> Ok true
        | Error error -> Error error
    | Idle ->
        let panEnabled =
            (state.routing.shift_right_click_pan && shift_down ())
            || (state.routing.alt_right_click_pan && alt_down ())

        let pivotEnabled =
            (state.routing.shift_right_click && shift_down ())
            || (state.routing.alt_right_click && alt_down ())

        let requestedMode =
            if panEnabled then Some Pan
            elif pivotEnabled then Some Pivot
            else None

        match requestedMode with
        | Some mode when state.routing.enabled && not state.suspended ->
            state.view_latch <- WaitingForRelease(root_window window, mode)
            keep_timer_running ()
            Ok true
        | Some _
        | None -> Ok false

let right_click_enabled () =
    view_latch_engaged ()
    || (state.routing.enabled
        && (state.routing.shift_right_click
            || state.routing.alt_right_click
            || state.routing.shift_right_click_pan
            || state.routing.alt_right_click_pan))

let begin_view_latch (window: nativeint) (mode: ViewLatchMode) =
    if any_routed () then
        state.view_latch <- Active(window, false, mode)
    else
        let result =
            match mode with
            | Pivot -> Win32.send_middle_mouse true
            | Pan ->
                match Win32.send_shift_key true with
                | Error error -> Error error
                | Ok() ->
                    state.synthetic_shift_down <- true

                    match Win32.send_middle_mouse true with
                    | Ok() -> Ok()
                    | Error error ->
                        release_synthetic_shift () |> ignore
                        Error error

        match result with
        | Ok() -> state.view_latch <- Active(window, false, mode)
        | Error error ->
            state.view_latch <- Idle
            stop_timer_if_idle ()
            Debug.WriteLine $"RhinosCanFly latched view manipulation: {error}"

let update_view_latch () =
    match state.view_latch with
    | Idle -> ()
    | WaitingForRelease(window, mode) ->
        if Win32.GetForegroundWindow() <> window then
            state.view_latch <- Idle
            stop_timer_if_idle ()
        elif
            Win32.GetAsyncKeyState Win32.VK_RBUTTON >= 0s
            && not (shift_down ())
            && not (alt_down ())
        then
            begin_view_latch window mode
    | Restarting(window, _, mode) ->
        if Win32.GetForegroundWindow() <> window then
            state.view_latch <- Idle
            stop_timer_if_idle ()
        elif any_routed () then
            state.view_latch <- Active(window, view_modifier_down (), mode)
        else
            let modifiersDown = view_modifier_down ()
            state.view_latch <- Restarting(window, modifiersDown, mode)

            match Win32.send_middle_mouse true with
            | Ok() -> state.view_latch <- Active(window, modifiersDown, mode)
            | Error error -> Debug.WriteLine $"RhinosCanFly latched view manipulation: {error}"
    | Active(window, previousModifiersDown, mode) ->
        if Win32.GetForegroundWindow() <> window then
            match release_view_latch () with
            | Ok() -> ()
            | Error error -> Debug.WriteLine $"RhinosCanFly latched view manipulation: {error}"
        else
            let modifiersDown = view_modifier_down ()

            if mode = Pivot && modifiersDown <> previousModifiersDown && not (any_routed ()) then
                match Win32.send_middle_mouse false with
                | Error error -> Debug.WriteLine $"RhinosCanFly latched view manipulation: {error}"
                | Ok() ->
                    state.view_latch <- Restarting(window, modifiersDown, mode)

                    match Win32.send_middle_mouse true with
                    | Ok() -> state.view_latch <- Active(window, modifiersDown, mode)
                    | Error error -> Debug.WriteLine $"RhinosCanFly latched view manipulation: {error}"

let update_routed_modifiers () =
    if any_routed () && not (view_latch_engaged ()) then
        let modifiersDown = view_modifier_down ()

        if modifiersDown <> state.routed_modifiers_down then
            match Win32.send_middle_mouse false with
            | Error error -> Debug.WriteLine $"RhinosCanFly mouse override: {error}"
            | Ok() ->
                match Win32.send_middle_mouse true with
                | Ok() -> state.routed_modifiers_down <- modifiersDown
                | Error error -> Debug.WriteLine $"RhinosCanFly mouse override: {error}"

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

        update_view_latch ()
        update_routed_modifiers ()
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
                  mouse5 = source.mouse5_acts_as_middle
                  shift_right_click = source.shift_right_click_toggles_view
                  alt_right_click = source.alt_right_click_toggles_view
                  shift_right_click_pan = source.shift_right_click_pans
                  alt_right_click_pan = source.alt_right_click_pans }

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
