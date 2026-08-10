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
    | Hold
    | Toggle

type RoutingConfig =
    { mouse4: SideButtonMode
      mouse5: SideButtonMode
      shift_right_click: ViewLatchMode option
      alt_right_click: ViewLatchMode option
      exit: KeyBinding option
      exit_on_mouse_left: bool
      exit_on_mouse_right: bool }

type SideButton =
    | Mouse4
    | Mouse5

type SideButtonState =
    | Released
    | WaitingForDrag of start: Point * window: nativeint
    | HoldActive of window: nativeint
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
      mutable side_button_restart_pending: bool
      mutable middle_mouse_modifiers_down: bool }

let dragSize = SystemInformation.DragSize

[<Literal>]
let pollTimerIntervalMilliseconds = 15

let pollTimer = new Timer(Interval = pollTimerIntervalMilliseconds)

let state =
    { routing =
        { mouse4 = Disabled
          mouse5 = Disabled
          shift_right_click = None
          alt_right_click = None
          exit = None
          exit_on_mouse_left = false
          exit_on_mouse_right = false }
      lifecycle = Available
      mouse4 = Released
      mouse5 = Released
      view_latch = NoViewLatch
      synthetic_shift = ShiftReleased
      side_button_restart_pending = false
      middle_mouse_modifiers_down = false }

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
    match button with
    | Mouse4 -> state.routing.mouse4
    | Mouse5 -> state.routing.mouse5

let side_button_routing_enabled () =
    state.routing.mouse4 <> Disabled || state.routing.mouse5 <> Disabled

let is_down (button: SideButton) =
    Win32.GetAsyncKeyState(int (key button)) < 0s

let button_holds_middle (buttonState: SideButtonState) =
    match buttonState with
    | HoldActive _
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

let exit_key_down () =
    match state.routing.exit with
    | Some binding -> PlatformBindings.is_down binding
    | None -> false

let exit_binding_contains (virtualKey: int) =
    match state.routing.exit with
    | Some binding -> List.contains virtualKey binding.virtual_keys
    | None -> false

let left_mouse_exit_enabled () =
    state.routing.exit_on_mouse_left || exit_binding_contains Win32.VK_LBUTTON

let right_mouse_exit_enabled () =
    state.routing.exit_on_mouse_right || exit_binding_contains Win32.VK_RBUTTON

let release_synthetic_shift () =
    match state.synthetic_shift with
    | ShiftReleased -> Ok()
    | ShiftPressed ->
        match Win32.send_shift_key false with
        | Ok() ->
            state.synthetic_shift <- ShiftReleased
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
    if not pollTimer.Enabled then
        pollTimer.Start()

let stop_timer_if_idle () =
    if not (any_button_engaged ()) && not (view_latch_engaged ()) then
        pollTimer.Stop()

let begin_hold (button: SideButton) (window: nativeint) =
    if middle_mouse_down () then
        set_button_state button (HoldActive window)
    else
        match Win32.send_middle_mouse true with
        | Ok() ->
            set_button_state button (HoldActive window)
            state.side_button_restart_pending <- false
            state.middle_mouse_modifiers_down <- view_modifier_down ()
        | Error error -> Debug.WriteLine $"RhinosCanFly mouse override: {error}"

let finish_button (button: SideButton) =
    match get_button_state button with
    | Released -> ()
    | WaitingForDrag _ -> set_button_state button Released
    | TogglePressed window -> set_button_state button (ToggleLatched window)
    | ToggleLatched _ -> ()
    | ToggleReleasePressed -> set_button_state button Released
    | HoldActive window ->
        set_button_state button Released

        if not (middle_mouse_down ()) then
            let releaseResult =
                if state.side_button_restart_pending then
                    state.side_button_restart_pending <- false
                    Ok()
                else
                    Win32.send_middle_mouse false

            match releaseResult with
            | Ok() ->
                state.middle_mouse_modifiers_down <- false

                match release_synthetic_shift () with
                | Ok() -> ()
                | Error error -> Debug.WriteLine $"RhinosCanFly mouse override: {error}"
            | Error error ->
                set_button_state button (HoldActive window)
                Debug.WriteLine $"RhinosCanFly mouse override: {error}"

    stop_timer_if_idle ()

let release_all () =
    let previousMouse4 = state.mouse4
    let previousMouse5 = state.mouse5
    let previousViewLatch = state.view_latch
    let hadMiddleMouseDown = middle_mouse_down ()
    let restartPending = state.side_button_restart_pending

    state.mouse4 <- Released
    state.mouse5 <- Released
    state.view_latch <- NoViewLatch
    state.side_button_restart_pending <- false

    if not hadMiddleMouseDown || restartPending then
        match release_synthetic_shift () with
        | Ok() ->
            state.middle_mouse_modifiers_down <- false
            pollTimer.Stop()
            Ok()
        | Error error ->
            state.mouse4 <- previousMouse4
            state.mouse5 <- previousMouse5
            state.view_latch <- previousViewLatch
            state.side_button_restart_pending <- restartPending
            Error error
    else
        let releaseResult =
            match state.synthetic_shift with
            | ShiftPressed -> Win32.stop_shift_middle_mouse ()
            | ShiftReleased -> Win32.send_middle_mouse false

        match releaseResult with
        | Error error ->
            state.mouse4 <- previousMouse4
            state.mouse5 <- previousMouse5
            state.view_latch <- previousViewLatch
            state.side_button_restart_pending <- restartPending
            Error error
        | Ok() ->
            state.synthetic_shift <- ShiftReleased
            state.middle_mouse_modifiers_down <- false
            pollTimer.Stop()
            Ok()

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
        if any_button_holds_middle () then
            release_all ()
        else
            state.view_latch <- NoViewLatch

            let releaseResult =
                match active, state.synthetic_shift with
                | PanActive _, ShiftPressed -> Win32.stop_shift_middle_mouse ()
                | _ -> Win32.send_middle_mouse false

            match releaseResult with
            | Ok() ->
                state.synthetic_shift <- ShiftReleased
                stop_timer_if_idle ()
                Ok()
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

let view_latch_input_released () =
    Win32.GetAsyncKeyState Win32.VK_RBUTTON >= 0s
    && not (shift_down ())
    && not (alt_down ())

let handle_right_click (window: nativeint) =
    if
        right_mouse_exit_enabled ()
        && (state.routing.exit_on_mouse_right || exit_key_down ())
        && any_button_engaged ()
    then
        match release_all () with
        | Ok() -> Ok true
        | Error error -> Error error
    else
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
            | Some mode when state.lifecycle = Available ->
                let released = if any_button_engaged () then release_all () else Ok()

                match released with
                | Error error -> Error error
                | Ok() ->
                    state.view_latch <-
                        WaitingForRelease
                            { window = root_window window
                              mode = mode }

                    keep_timer_running ()
                    Ok true
            | Some _
            | None -> Ok false

let right_click_enabled () =
    right_mouse_exit_enabled ()
    || Option.isSome state.routing.shift_right_click
    || Option.isSome state.routing.alt_right_click

let begin_view_latch (window: nativeint) (mode: ViewLatchMode) =
    if any_button_holds_middle () then
        Error "Another mouse override already owns the middle mouse button."
    else
        let result =
            match mode with
            | Pivot -> Win32.send_middle_mouse true
            | Pan -> Win32.start_shift_middle_mouse ()

        match result with
        | Ok() ->
            state.synthetic_shift <-
                match mode with
                | Pivot -> ShiftReleased
                | Pan -> ShiftPressed

            state.view_latch <-
                match mode with
                | Pivot ->
                    PivotActive
                        { window = window
                          modifiers_down = false }
                | Pan -> PanActive window

            keep_timer_running ()
            Ok()
        | Error error ->
            state.view_latch <- NoViewLatch
            stop_timer_if_idle ()
            Error error

let update_view_latch () =
    match state.view_latch with
    | NoViewLatch -> ()
    | WaitingForRelease pending ->
        if Win32.GetForegroundWindow() <> pending.window then
            state.view_latch <- NoViewLatch
            stop_timer_if_idle ()
        elif view_latch_input_released () then
            match begin_view_latch pending.window pending.mode with
            | Ok() -> ()
            | Error error -> Debug.WriteLine $"RhinosCanFly latched view manipulation: {error}"
    | RetryingPivot window ->
        if Win32.GetForegroundWindow() <> window then
            state.view_latch <- NoViewLatch
            stop_timer_if_idle ()
        elif any_button_holds_middle () then
            match release_all () with
            | Ok() -> ()
            | Error error -> Debug.WriteLine $"RhinosCanFly latched view manipulation: {error}"
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
                | Ok() -> state.view_latch <- RetryingPivot active.window
    | PanActive window ->
        if Win32.GetForegroundWindow() <> window then
            match release_view_latch () with
            | Ok() -> ()
            | Error error -> Debug.WriteLine $"RhinosCanFly latched view manipulation: {error}"

let update_middle_mouse_modifiers () =
    if any_button_holds_middle () && not (view_latch_engaged ()) then
        let modifiersDown = view_modifier_down ()

        if state.side_button_restart_pending then
            match Win32.send_middle_mouse true with
            | Ok() ->
                state.side_button_restart_pending <- false
                state.middle_mouse_modifiers_down <- modifiersDown
            | Error error -> Debug.WriteLine $"RhinosCanFly mouse override: {error}"
        elif modifiersDown <> state.middle_mouse_modifiers_down then
            match Win32.send_middle_mouse false with
            | Ok() -> state.side_button_restart_pending <- true
            | Error error -> Debug.WriteLine $"RhinosCanFly mouse override: {error}"

let begin_drag (button: SideButton) (position: Point) (window: nativeint) =
    if get_button_state button = Released then
        set_button_state button (WaitingForDrag(position, root_window window))
        keep_timer_running ()

let update_hold_drag (button: SideButton) (position: Point) =
    match get_button_state button with
    | WaitingForDrag(start, window) when moved_enough start position -> begin_hold button window
    | Released
    | WaitingForDrag _
    | HoldActive _
    | TogglePressed _
    | ToggleLatched _
    | ToggleReleasePressed -> ()

let stop_toggle (button: SideButton) (nextState: SideButtonState) =
    let previous = get_button_state button
    set_button_state button nextState

    if middle_mouse_down () then
        stop_timer_if_idle ()
    else
        let releaseResult =
            if state.side_button_restart_pending then
                state.side_button_restart_pending <- false
                Ok()
            else
                Win32.send_middle_mouse false

        match releaseResult with
        | Ok() ->
            state.middle_mouse_modifiers_down <- false
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
                state.side_button_restart_pending <- false
                state.middle_mouse_modifiers_down <- view_modifier_down ()
                start ()
            | Error error -> Debug.WriteLine $"RhinosCanFly mouse override: {error}"
    | ToggleLatched _ -> stop_toggle button ToggleReleasePressed
    | WaitingForDrag _
    | HoldActive _
    | TogglePressed _
    | ToggleReleasePressed -> ()

let handle_button_down (button: SideButton) (position: Point) (window: nativeint) =
    let mode = mode_for button

    if view_latch_engaged () then
        match release_view_latch () with
        | Error error -> Debug.WriteLine $"RhinosCanFly mouse override: {error}"
        | Ok() ->
            match mode with
            | Disabled -> ()
            | Hold -> begin_drag button position window
            | Toggle ->
                set_button_state button ToggleReleasePressed
                keep_timer_running ()
    else
        match mode with
        | Disabled -> ()
        | Hold -> begin_drag button position window
        | Toggle -> toggle_button button window

let event_root_window (event: MouseCallbackEventArgs) =
    if isNull event.View then
        Win32.GetForegroundWindow()
    else
        root_window event.View.Handle

let update_button_from_move (button: SideButton) (event: MouseCallbackEventArgs) =
    match mode_for button, view_latch_engaged () with
    | _, true -> ()
    | Disabled, false -> ()
    | Hold, false ->
        if is_down button then
            begin_drag button event.ViewportPoint (event_root_window event)
            update_hold_drag button event.ViewportPoint
        else
            finish_button button
    | Toggle, false ->
        match get_button_state button, is_down button with
        | Released, true
        | ToggleLatched _, true -> toggle_button button (event_root_window event)
        | TogglePressed _, false
        | ToggleReleasePressed, false -> finish_button button
        | Released, false
        | WaitingForDrag _, _
        | HoldActive _, _
        | TogglePressed _, true
        | ToggleLatched _, false
        | ToggleReleasePressed, true -> ()

type SideButtonCallback() =
    inherit MouseCallback()

    override _.OnMouseDown(event: MouseCallbackEventArgs) =
        try
            let exitsNavigation =
                left_mouse_exit_enabled ()
                && (state.routing.exit_on_mouse_left || exit_key_down ())
                && event.MouseButton = Rhino.UI.MouseButton.Left
                && (any_button_engaged () || view_latch_engaged ())

            if exitsNavigation then
                event.Cancel <- true

                match release_all () with
                | Ok() -> ()
                | Error error -> Debug.WriteLine $"RhinosCanFly mouse override exit: {error}"
            else
                if event_has_button Mouse4 event.Button then
                    handle_button_down Mouse4 event.ViewportPoint (event_root_window event)

                if event_has_button Mouse5 event.Button then
                    handle_button_down Mouse5 event.ViewportPoint (event_root_window event)
        with error ->
            Debug.WriteLine $"RhinosCanFly mouse override callback: {error.Message}"

    override _.OnMouseMove(event: MouseCallbackEventArgs) =
        try
            if side_button_routing_enabled () then
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

let refresh_callback_enabled () =
    callback.Enabled <-
        state.lifecycle = Available
        && (side_button_routing_enabled () || left_mouse_exit_enabled ())

let side_button_lost_focus (foreground: nativeint) (buttonState: SideButtonState) =
    match buttonState with
    | WaitingForDrag(_, window)
    | HoldActive window
    | TogglePressed window
    | ToggleLatched window -> foreground <> window
    | Released
    | ToggleReleasePressed -> false

let poll_button (button: SideButton) =
    match get_button_state button with
    | Released -> ()
    | WaitingForDrag _
    | HoldActive _
    | TogglePressed _
    | ToggleReleasePressed when not (is_down button) -> finish_button button
    | ToggleLatched window when Win32.GetForegroundWindow() <> window -> stop_toggle button Released
    | ToggleLatched _ when is_down button -> stop_toggle button ToggleReleasePressed
    | WaitingForDrag _
    | HoldActive _
    | TogglePressed _
    | ToggleLatched _
    | ToggleReleasePressed -> ()

pollTimer.Tick.Add(fun (_: EventArgs) ->
    try
        let foreground = Win32.GetForegroundWindow()

        if
            side_button_lost_focus foreground state.mouse4
            || side_button_lost_focus foreground state.mouse5
        then
            match release_all () with
            | Ok() -> ()
            | Error error -> Debug.WriteLine $"RhinosCanFly mouse override focus loss: {error}"
        elif exit_key_down () then
            match release_all () with
            | Ok() -> ()
            | Error error -> Debug.WriteLine $"RhinosCanFly mouse override exit: {error}"
        else
            poll_button Mouse4
            poll_button Mouse5

            update_view_latch ()
            update_middle_mouse_modifiers ()
    with error ->
        Debug.WriteLine $"RhinosCanFly mouse override timer: {error.Message}")

let configured_view_latch_mode (mode: ModifiedRightClickMode) =
    match mode with
    | ModifiedRightClickMode.Off -> None
    | ModifiedRightClickMode.Pivot -> Some Pivot
    | ModifiedRightClickMode.Pan -> Some Pan
    | _ -> None

let current_view_latch_mode () =
    match state.view_latch with
    | NoViewLatch -> None
    | WaitingForRelease pending -> Some pending.mode
    | RetryingPivot _
    | PivotActive _ -> Some Pivot
    | PanActive _ -> Some Pan

let view_latch_is (mode: ViewLatchMode) = current_view_latch_mode () = Some mode

let start_view_latch (window: nativeint) (mode: ViewLatchMode) =
    state.view_latch <-
        WaitingForRelease
            { window = root_window window
              mode = mode }

    keep_timer_running ()
    Ok()

let toggle_view_latch (window: nativeint) (mode: ViewLatchMode) =
    if state.lifecycle <> Available then
        Error "Mouse button overrides are unavailable."
    else
        match current_view_latch_mode () with
        | Some current when current = mode -> release_all ()
        | None when not (any_button_holds_middle ()) -> start_view_latch window mode
        | Some _
        | None ->
            match release_all () with
            | Error error -> Error error
            | Ok() -> start_view_latch window mode

let configured_side_button_mode (mode: MouseButtonPivotMode) =
    match mode with
    | MouseButtonPivotMode.Off -> Disabled
    | MouseButtonPivotMode.Hold -> Hold
    | MouseButtonPivotMode.Toggle -> Toggle
    | _ -> Disabled

let apply (source: FlyConfigFile) (exitBinding: KeyBinding) =
    if state.lifecycle = ShutDown then
        Error "Mouse button overrides have already shut down."
    else
        match release_all () with
        | Error error -> Error error
        | Ok() ->
            state.routing <-
                { mouse4 = configured_side_button_mode source.mouse4_pivot_mode
                  mouse5 = configured_side_button_mode source.mouse5_pivot_mode
                  shift_right_click = configured_view_latch_mode source.shift_right_click_mode
                  alt_right_click = configured_view_latch_mode source.alt_right_click_mode
                  exit = if source.enabled then Some exitBinding else None
                  exit_on_mouse_left = source.exit_on_mouse_left
                  exit_on_mouse_right = source.exit_on_mouse_right }

            refresh_callback_enabled ()

            Ok()

let suspend () =
    if state.lifecycle = ShutDown then
        Error "Mouse button overrides have already shut down."
    else
        callback.Enabled <- false

        match release_all () with
        | Ok() ->
            state.lifecycle <- Suspended
            Ok()
        | Error error ->
            refresh_callback_enabled ()
            Error error

let resume () =
    if state.lifecycle <> ShutDown then
        state.lifecycle <- Available

        refresh_callback_enabled ()

let shutdown () =
    if state.lifecycle <> ShutDown then
        state.lifecycle <- ShutDown
        callback.Enabled <- false

        match release_all () with
        | Ok() -> ()
        | Error error -> Debug.WriteLine $"RhinosCanFly mouse override shutdown: {error}"

        pollTimer.Dispose()
