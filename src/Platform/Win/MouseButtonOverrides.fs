module RhinosCanFly.Platform.Win.MouseButtonOverrides

open System
open System.Diagnostics
open System.Drawing
open System.Windows.Forms
open RhinosCanFly
open Rhino.UI

type ViewLatchMode =
    | Pivot
    | Pan

type SideButtonMode =
    | Disabled
    | Drag
    | Toggle

type RoutingConfig =
    { enabled: bool
      mouse4: SideButtonMode
      mouse5: SideButtonMode
      shift_right_click: ViewLatchMode option
      alt_right_click: ViewLatchMode option }

type SideButton =
    | Mouse4
    | Mouse5

type SideButtonState =
    | Released
    | WaitingForDrag of Point
    | Routed
    | TogglePressed of window: nativeint
    | ToggleLatched of window: nativeint
    | ToggleReleasePressed

type PendingViewLatch =
    { window: nativeint
      mode: ViewLatchMode }

type PivotViewLatch =
    { window: nativeint
      modifiers_down: bool }

type ViewLatch =
    | NoViewLatch
    | WaitingForRelease of PendingViewLatch
    | RetryingPivot of window: nativeint
    | PivotActive of PivotViewLatch
    | PanActive of nativeint

type SyntheticShiftState =
    | ShiftReleased
    | ShiftPressed

type OverrideLifecycle =
    | Available
    | Suspended
    | ShutDown

type State =
    { mutable routing: RoutingConfig
      mutable lifecycle: OverrideLifecycle
      mutable mouse4: SideButtonState
      mutable mouse5: SideButtonState
      mutable view_latch: ViewLatch
      mutable synthetic_shift: SyntheticShiftState
      mutable routed_modifiers_down: bool }

let dragSize = SystemInformation.DragSize

[<Literal>]
let releaseTimerIntervalMilliseconds = 15

let releaseTimer = new Timer(Interval = releaseTimerIntervalMilliseconds)

let state =
    { routing =
        { enabled = false
          mouse4 = Disabled
          mouse5 = Disabled
          shift_right_click = None
          alt_right_click = None }
      lifecycle = Available
      mouse4 = Released
      mouse5 = Released
      view_latch = NoViewLatch
      synthetic_shift = ShiftReleased
      routed_modifiers_down = false }

let get_button_state (button: SideButton) =
    match button with
    | Mouse4 -> state.mouse4
    | Mouse5 -> state.mouse5

let set_button_state (button: SideButton) (buttonState: SideButtonState) =
    match button with
    | Mouse4 -> state.mouse4 <- buttonState
    | Mouse5 -> state.mouse5 <- buttonState

let key (button: SideButton) =
    match button with
    | Mouse4 -> Keys.XButton1
    | Mouse5 -> Keys.XButton2

let mode_for (button: SideButton) =
    if state.routing.enabled then
        match button with
        | Mouse4 -> state.routing.mouse4
        | Mouse5 -> state.routing.mouse5
    else
        Disabled

let is_down (button: SideButton) =
    Win32.GetAsyncKeyState(int (key button)) < 0s

let button_holds_middle (buttonState: SideButtonState) =
    match buttonState with
    | Routed
    | TogglePressed _
    | ToggleLatched _ -> true
    | Released
    | WaitingForDrag _
    | ToggleReleasePressed -> false

let any_button_holds_middle () =
    button_holds_middle state.mouse4 || button_holds_middle state.mouse5

let any_button_engaged () =
    state.mouse4 <> Released || state.mouse5 <> Released

let view_latch_active () =
    match state.view_latch with
    | PivotActive _
    | PanActive _ -> true
    | NoViewLatch
    | WaitingForRelease _
    | RetryingPivot _ -> false

let view_latch_engaged () =
    match state.view_latch with
    | NoViewLatch -> false
    | WaitingForRelease _
    | RetryingPivot _
    | PivotActive _
    | PanActive _ -> true

let middle_mouse_down () =
    any_button_holds_middle () || view_latch_active ()

let release_synthetic_shift () =
    match state.synthetic_shift with
    | ShiftReleased -> Ok()
    | ShiftPressed ->
        match Win32.send_shift_key false with
        | Ok() ->
            state.synthetic_shift <- ShiftReleased
            Ok()
        | Error error -> Error error

let press_synthetic_shift () =
    match state.synthetic_shift with
    | ShiftPressed -> Ok()
    | ShiftReleased ->
        match Win32.send_shift_key true with
        | Ok() ->
            state.synthetic_shift <- ShiftPressed
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

let event_has_button (button: SideButton) (buttons: MouseButtons) =
    let expected =
        match button with
        | Mouse4 -> MouseButtons.XButton1
        | Mouse5 -> MouseButtons.XButton2

    buttons &&& expected = expected

let keep_timer_running () =
    if not releaseTimer.Enabled then
        releaseTimer.Start()

let stop_timer_if_idle () =
    if not (any_button_engaged ()) && not (view_latch_engaged ()) then
        releaseTimer.Stop()

let begin_route (button: SideButton) =
    if middle_mouse_down () then
        set_button_state button Routed
    else
        match Win32.send_middle_mouse true with
        | Ok() ->
            set_button_state button Routed
            state.routed_modifiers_down <- view_modifier_down ()
        | Error error -> Debug.WriteLine $"RhinosCanFly mouse override: {error}"

let finish_button (button: SideButton) =
    match get_button_state button with
    | Released -> ()
    | WaitingForDrag _ -> set_button_state button Released
    | TogglePressed window -> set_button_state button (ToggleLatched window)
    | ToggleLatched _ -> ()
    | ToggleReleasePressed -> set_button_state button Released
    | Routed ->
        set_button_state button Released

        if not (middle_mouse_down ()) then
            match Win32.send_middle_mouse false with
            | Ok() ->
                state.routed_modifiers_down <- false

                match release_synthetic_shift () with
                | Ok() -> ()
                | Error error -> Debug.WriteLine $"RhinosCanFly mouse override: {error}"
            | Error error ->
                set_button_state button Routed
                Debug.WriteLine $"RhinosCanFly mouse override: {error}"

    stop_timer_if_idle ()

let release_all () =
    let previousMouse4 = state.mouse4
    let previousMouse5 = state.mouse5
    let previousViewLatch = state.view_latch
    let hadMiddleMouseDown = middle_mouse_down ()

    state.mouse4 <- Released
    state.mouse5 <- Released
    state.view_latch <- NoViewLatch

    if not hadMiddleMouseDown then
        releaseTimer.Stop()
        release_synthetic_shift ()
    else
        match Win32.send_middle_mouse false with
        | Error error ->
            state.mouse4 <- previousMouse4
            state.mouse5 <- previousMouse5
            state.view_latch <- previousViewLatch
            Error error
        | Ok() ->
            state.routed_modifiers_down <- false
            releaseTimer.Stop()
            release_synthetic_shift ()

let root_window (window: nativeint) =
    let root = Win32.GetAncestor(window, Win32.GA_ROOT)

    if root = nativeint 0 then window else root

let release_view_latch () =
    match state.view_latch with
    | NoViewLatch -> Ok()
    | WaitingForRelease _ ->
        state.view_latch <- NoViewLatch
        stop_timer_if_idle ()
        Ok()
    | RetryingPivot _ ->
        state.view_latch <- NoViewLatch
        stop_timer_if_idle ()
        Ok()
    | (PivotActive _ | PanActive _) as active ->
        state.view_latch <- NoViewLatch

        if any_button_holds_middle () then
            stop_timer_if_idle ()
            release_synthetic_shift ()
        else
            match Win32.send_middle_mouse false with
            | Ok() ->
                stop_timer_if_idle ()
                release_synthetic_shift ()
            | Error error ->
                state.view_latch <- active
                Error error

let requested_view_latch_mode () =
    let shiftRoute =
        if shift_down () then
            state.routing.shift_right_click
        else
            None

    let altRoute = if alt_down () then state.routing.alt_right_click else None

    match shiftRoute, altRoute with
    | Some Pan, _
    | _, Some Pan -> Some Pan
    | Some Pivot, _
    | _, Some Pivot -> Some Pivot
    | None, None -> None

let handle_right_click (window: nativeint) =
    match state.view_latch with
    | PivotActive _
    | PanActive _
    | WaitingForRelease _
    | RetryingPivot _ ->
        match release_view_latch () with
        | Ok() -> Ok true
        | Error error -> Error error
    | NoViewLatch ->
        match requested_view_latch_mode () with
        | Some mode when state.routing.enabled && state.lifecycle = Available ->
            state.view_latch <-
                WaitingForRelease
                    { window = root_window window
                      mode = mode }

            keep_timer_running ()
            Ok true
        | Some _
        | None -> Ok false

let right_click_enabled () =
    view_latch_engaged ()
    || (state.routing.enabled
        && (Option.isSome state.routing.shift_right_click
            || Option.isSome state.routing.alt_right_click))

let begin_view_latch (window: nativeint) (mode: ViewLatchMode) =
    if any_button_holds_middle () then
        state.view_latch <-
            match mode with
            | Pivot ->
                PivotActive
                    { window = window
                      modifiers_down = false }
            | Pan -> PanActive window
    else
        let result =
            match mode with
            | Pivot -> Win32.send_middle_mouse true
            | Pan ->
                let shiftResult = if shift_down () then Ok() else press_synthetic_shift ()

                match shiftResult with
                | Error error -> Error error
                | Ok() ->
                    match Win32.send_middle_mouse true with
                    | Ok() -> Ok()
                    | Error error ->
                        release_synthetic_shift () |> ignore
                        Error error

        match result with
        | Ok() ->
            state.view_latch <-
                match mode with
                | Pivot ->
                    PivotActive
                        { window = window
                          modifiers_down = false }
                | Pan -> PanActive window
        | Error error ->
            state.view_latch <- NoViewLatch
            stop_timer_if_idle ()
            Debug.WriteLine $"RhinosCanFly latched view manipulation: {error}"

let update_view_latch () =
    match state.view_latch with
    | NoViewLatch -> ()
    | WaitingForRelease pending ->
        if Win32.GetForegroundWindow() <> pending.window then
            state.view_latch <- NoViewLatch
            stop_timer_if_idle ()
        elif
            Win32.GetAsyncKeyState Win32.VK_RBUTTON >= 0s
            && (match pending.mode with
                | Pan -> not (alt_down ())
                | Pivot -> not (shift_down ()) && not (alt_down ()))
        then
            begin_view_latch pending.window pending.mode
    | RetryingPivot window ->
        if Win32.GetForegroundWindow() <> window then
            state.view_latch <- NoViewLatch
            stop_timer_if_idle ()
        elif any_button_holds_middle () then
            state.view_latch <-
                PivotActive
                    { window = window
                      modifiers_down = view_modifier_down () }
        else
            let modifiersDown = view_modifier_down ()

            match Win32.send_middle_mouse true with
            | Ok() ->
                state.view_latch <-
                    PivotActive
                        { window = window
                          modifiers_down = modifiersDown }
            | Error error -> Debug.WriteLine $"RhinosCanFly latched view manipulation: {error}"
    | PivotActive active ->
        if Win32.GetForegroundWindow() <> active.window then
            match release_view_latch () with
            | Ok() -> ()
            | Error error -> Debug.WriteLine $"RhinosCanFly latched view manipulation: {error}"
        else
            let modifiersDown = view_modifier_down ()

            if modifiersDown <> active.modifiers_down && not (any_button_holds_middle ()) then
                match Win32.send_middle_mouse false with
                | Error error -> Debug.WriteLine $"RhinosCanFly latched view manipulation: {error}"
                | Ok() ->
                    state.view_latch <- RetryingPivot active.window

                    match Win32.send_middle_mouse true with
                    | Ok() ->
                        state.view_latch <-
                            PivotActive
                                { window = active.window
                                  modifiers_down = modifiersDown }
                    | Error error -> Debug.WriteLine $"RhinosCanFly latched view manipulation: {error}"
    | PanActive window ->
        if Win32.GetForegroundWindow() <> window then
            match release_view_latch () with
            | Ok() -> ()
            | Error error -> Debug.WriteLine $"RhinosCanFly latched view manipulation: {error}"
        elif state.synthetic_shift = ShiftReleased && not (shift_down ()) then
            match press_synthetic_shift () with
            | Ok() -> ()
            | Error error -> Debug.WriteLine $"RhinosCanFly latched view manipulation: {error}"

let update_routed_modifiers () =
    if any_button_holds_middle () && not (view_latch_engaged ()) then
        let modifiersDown = view_modifier_down ()

        if modifiersDown <> state.routed_modifiers_down then
            match Win32.send_middle_mouse false with
            | Error error -> Debug.WriteLine $"RhinosCanFly mouse override: {error}"
            | Ok() ->
                match Win32.send_middle_mouse true with
                | Ok() -> state.routed_modifiers_down <- modifiersDown
                | Error error -> Debug.WriteLine $"RhinosCanFly mouse override: {error}"

let begin_drag (button: SideButton) (position: Point) =
    if get_button_state button = Released then
        set_button_state button (WaitingForDrag position)
        keep_timer_running ()

let update_button_drag (button: SideButton) (position: Point) =
    match get_button_state button with
    | WaitingForDrag start when moved_enough start position -> begin_route button
    | Released
    | WaitingForDrag _
    | Routed
    | TogglePressed _
    | ToggleLatched _
    | ToggleReleasePressed -> ()

let stop_toggle (button: SideButton) (nextState: SideButtonState) =
    let previous = get_button_state button
    set_button_state button nextState

    if middle_mouse_down () then
        stop_timer_if_idle ()
    else
        match Win32.send_middle_mouse false with
        | Ok() ->
            state.routed_modifiers_down <- false
            stop_timer_if_idle ()

            match release_synthetic_shift () with
            | Ok() -> ()
            | Error error -> Debug.WriteLine $"RhinosCanFly mouse override: {error}"
        | Error error ->
            set_button_state button previous
            Debug.WriteLine $"RhinosCanFly mouse override: {error}"

let toggle_button (button: SideButton) (window: nativeint) =
    match get_button_state button with
    | Released ->
        let start () =
            set_button_state button (TogglePressed(root_window window))
            keep_timer_running ()

        if middle_mouse_down () then
            start ()
        else
            match Win32.send_middle_mouse true with
            | Ok() ->
                state.routed_modifiers_down <- view_modifier_down ()
                start ()
            | Error error -> Debug.WriteLine $"RhinosCanFly mouse override: {error}"
    | ToggleLatched _ -> stop_toggle button ToggleReleasePressed
    | WaitingForDrag _
    | Routed
    | TogglePressed _
    | ToggleReleasePressed -> ()

let handle_button_down (button: SideButton) (position: Point) (window: nativeint) =
    match mode_for button with
    | Disabled -> ()
    | Drag -> begin_drag button position
    | Toggle -> toggle_button button window

let event_root_window (event: MouseCallbackEventArgs) =
    if isNull event.View then
        Win32.GetForegroundWindow()
    else
        root_window event.View.Handle

let update_button_from_move (button: SideButton) (event: MouseCallbackEventArgs) =
    match mode_for button with
    | Disabled -> ()
    | Drag ->
        if is_down button then
            begin_drag button event.ViewportPoint
            update_button_drag button event.ViewportPoint
        else
            finish_button button
    | Toggle ->
        match get_button_state button, is_down button with
        | Released, true
        | ToggleLatched _, true -> toggle_button button (event_root_window event)
        | TogglePressed _, false
        | ToggleReleasePressed, false -> finish_button button
        | Released, false
        | WaitingForDrag _, _
        | Routed, _
        | TogglePressed _, true
        | ToggleLatched _, false
        | ToggleReleasePressed, true -> ()

type SideButtonCallback() =
    inherit MouseCallback()

    override _.OnMouseDown(event: MouseCallbackEventArgs) =
        try
            if event_has_button Mouse4 event.Button then
                handle_button_down Mouse4 event.ViewportPoint (event_root_window event)

            if event_has_button Mouse5 event.Button then
                handle_button_down Mouse5 event.ViewportPoint (event_root_window event)
        with error ->
            Debug.WriteLine $"RhinosCanFly mouse override callback: {error.Message}"

    override _.OnMouseMove(event: MouseCallbackEventArgs) =
        try
            update_button_from_move Mouse4 event
            update_button_from_move Mouse5 event
        with error ->
            Debug.WriteLine $"RhinosCanFly mouse override callback: {error.Message}"

    override _.OnMouseUp(event: MouseCallbackEventArgs) =
        try
            if event_has_button Mouse4 event.Button then
                finish_button Mouse4

            if event_has_button Mouse5 event.Button then
                finish_button Mouse5
        with error ->
            Debug.WriteLine $"RhinosCanFly mouse override callback: {error.Message}"

let callback = SideButtonCallback()

let poll_button (button: SideButton) =
    match get_button_state button with
    | Released -> ()
    | WaitingForDrag _
    | Routed
    | TogglePressed _
    | ToggleReleasePressed when not (is_down button) -> finish_button button
    | ToggleLatched window when Win32.GetForegroundWindow() <> window -> stop_toggle button Released
    | ToggleLatched _ when is_down button -> stop_toggle button ToggleReleasePressed
    | WaitingForDrag _
    | Routed
    | TogglePressed _
    | ToggleLatched _
    | ToggleReleasePressed -> ()

releaseTimer.Tick.Add(fun (_: EventArgs) ->
    try
        poll_button Mouse4
        poll_button Mouse5

        update_view_latch ()
        update_routed_modifiers ()
    with error ->
        Debug.WriteLine $"RhinosCanFly mouse override timer: {error.Message}")

let configured_view_latch_mode (pivotEnabled: bool) (panEnabled: bool) =
    if panEnabled then Some Pan
    elif pivotEnabled then Some Pivot
    else None

let configured_side_button_mode (dragEnabled: bool) (toggleEnabled: bool) =
    if toggleEnabled then Toggle
    elif dragEnabled then Drag
    else Disabled

let side_button_routing_enabled () =
    state.routing.mouse4 <> Disabled || state.routing.mouse5 <> Disabled

let apply (source: FlyConfigFile) =
    if state.lifecycle = ShutDown then
        Error "Mouse button overrides have already shut down."
    else
        match release_all () with
        | Error error -> Error error
        | Ok() ->
            state.routing <-
                { enabled = source.mouse_button_overrides_enabled
                  mouse4 = configured_side_button_mode source.mouse4_acts_as_middle source.mouse4_toggles_middle
                  mouse5 = configured_side_button_mode source.mouse5_acts_as_middle source.mouse5_toggles_middle
                  shift_right_click =
                    configured_view_latch_mode source.shift_right_click_toggles_view source.shift_right_click_pans
                  alt_right_click =
                    configured_view_latch_mode source.alt_right_click_toggles_view source.alt_right_click_pans }

            callback.Enabled <-
                state.lifecycle = Available
                && state.routing.enabled
                && side_button_routing_enabled ()

            Ok()

let suspend () =
    if state.lifecycle <> ShutDown then
        state.lifecycle <- Suspended
        callback.Enabled <- false

        match release_all () with
        | Ok() -> ()
        | Error error -> Debug.WriteLine $"RhinosCanFly mouse override suspend: {error}"

let resume () =
    if state.lifecycle <> ShutDown then
        state.lifecycle <- Available

        callback.Enabled <- state.routing.enabled && side_button_routing_enabled ()

let shutdown () =
    if state.lifecycle <> ShutDown then
        state.lifecycle <- ShutDown
        callback.Enabled <- false

        match release_all () with
        | Ok() -> ()
        | Error error -> Debug.WriteLine $"RhinosCanFly mouse override shutdown: {error}"

        releaseTimer.Dispose()
