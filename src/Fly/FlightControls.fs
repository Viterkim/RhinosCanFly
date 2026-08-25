module RhinosCanFly.FlightControls

[<Literal>]
let HOST_VALIDATION_INTERVAL_SECONDS = 0.1

[<Literal>]
let MIDDLE_BUTTON_BIT = 1

[<Literal>]
let MOUSE4_BUTTON_BIT = 2

[<Literal>]
let MOUSE5_BUTTON_BIT = 4

let is_optional_down (key: KeyBinding option) =
    match key with
    | Some binding -> PlatformFlightKeyboard.binding_is_down binding
    | None -> false

let current_mouse_hold_buttons (matches: RoutedMouseAction -> bool) (mouse: FlyingMouseConfig) =
    let mutable buttons = 0

    if matches mouse.middle_button && PlatformInput.middle_mouse_button_down () then
        buttons <- buttons ||| MIDDLE_BUTTON_BIT

    if matches mouse.mouse4 && PlatformInput.mouse4_button_down () then
        buttons <- buttons ||| MOUSE4_BUTTON_BIT

    if matches mouse.mouse5 && PlatformInput.mouse5_button_down () then
        buttons <- buttons ||| MOUSE5_BUTTON_BIT

    buttons

let speed_step (state: FlyState) (steps: SpeedStepCount) =
    state.speed <- FlightSpeed.step state.config.movement state.speed steps

let speed_steps (state: FlyState) (steps: int64) =
    let mutable remaining = steps

    while remaining > 0L do
        speed_step state (SpeedStepCount 1.)
        remaining <- remaining - 1L

    while remaining < 0L do
        speed_step state (SpeedStepCount -1.)
        remaining <- remaining + 1L

let has_keyboard_action (actions: InputAccumulator.KeyboardAction) (action: InputAccumulator.KeyboardAction) =
    int actions &&& int action <> 0

let explicit_exit_reason (state: FlyState) =
    if state.restore_camera_on_exit then
        ExplicitRestoreCamera
    else
        ExplicitKeepCamera

let apply_keyboard_actions (actions: InputAccumulator.KeyboardAction) (state: FlyState) =
    if has_keyboard_action actions InputAccumulator.KeyboardAction.CancelAndRestore then
        FlyState.request_exit ExplicitRestoreCamera state
        InputEffect.barrier ViewChange.none
    elif has_keyboard_action actions InputAccumulator.KeyboardAction.Exit then
        FlyState.request_exit (explicit_exit_reason state) state
        InputEffect.barrier ViewChange.none
    else
        if has_keyboard_action actions InputAccumulator.KeyboardAction.PivotToggle then
            state.latched_mouse_navigation <- MouseNavigationMode.toggle PivotNavigation state.latched_mouse_navigation

        if has_keyboard_action actions InputAccumulator.KeyboardAction.PanToggle then
            state.latched_mouse_navigation <- MouseNavigationMode.toggle PanNavigation state.latched_mouse_navigation

        if has_keyboard_action actions InputAccumulator.KeyboardAction.PivotHoldStarted then
            state.keyboard_pivot_held <- true

        if has_keyboard_action actions InputAccumulator.KeyboardAction.PivotHoldEnded then
            state.keyboard_pivot_held <- false

        if has_keyboard_action actions InputAccumulator.KeyboardAction.PanHoldStarted then
            state.keyboard_pan_held <- true

        if has_keyboard_action actions InputAccumulator.KeyboardAction.PanHoldEnded then
            state.keyboard_pan_held <- false

        let movement = state.config.movement

        if
            movement.boost_mode = KeyActivationMode.Toggle
            && has_keyboard_action actions InputAccumulator.KeyboardAction.BoostToggle
        then
            state.boost_enabled <- not state.boost_enabled

        if
            movement.slow_mode = KeyActivationMode.Toggle
            && has_keyboard_action actions InputAccumulator.KeyboardAction.SlowToggle
        then
            state.slow_enabled <- not state.slow_enabled

        if has_keyboard_action actions InputAccumulator.KeyboardAction.SpeedIncrease then
            speed_step state (SpeedStepCount 1.)

        if has_keyboard_action actions InputAccumulator.KeyboardAction.SpeedDecrease then
            speed_step state (SpeedStepCount -1.)

        let mutable retargetChange = ViewChange.none
        let mutable pointerBarrier = false

        if has_keyboard_action actions InputAccumulator.KeyboardAction.RetargetAllViews then
            pointerBarrier <- state.config.behavior.retarget.keyboard_all_views <> RetargetMode.Off

            retargetChange <-
                FlightCamera.apply_retarget_request
                    RetargetScope.AllViews
                    state.config.behavior.retarget.keyboard_all_views
                    state
        elif has_keyboard_action actions InputAccumulator.KeyboardAction.RetargetOtherViews then
            pointerBarrier <- state.config.behavior.retarget.keyboard_other_views <> RetargetMode.Off

            retargetChange <-
                FlightCamera.apply_retarget_request
                    RetargetScope.OtherViews
                    state.config.behavior.retarget.keyboard_other_views
                    state

        if
            FlyState.is_running state
            && has_keyboard_action actions InputAccumulator.KeyboardAction.ProjectionToggle
        then
            FlightCamera.toggle_projection state
            pointerBarrier <- true

        { view_change = retargetChange
          pointer_barrier = pointerBarrier }

let set_mouse_hold (buttonBit: int) (down: bool) (action: RoutedMouseAction) (state: FlyState) =
    if RoutedMouseAction.holds_pivot action then
        if down then
            state.mouse_pivot_hold_buttons <- state.mouse_pivot_hold_buttons ||| buttonBit
        else
            state.mouse_pivot_hold_buttons <- state.mouse_pivot_hold_buttons &&& (~~~buttonBit)

    if RoutedMouseAction.holds_pan action then
        if down then
            state.mouse_pan_hold_buttons <- state.mouse_pan_hold_buttons ||| buttonBit
        else
            state.mouse_pan_hold_buttons <- state.mouse_pan_hold_buttons &&& (~~~buttonBit)

let apply_mouse_action_down (buttonBit: int) (action: RoutedMouseAction) (state: FlyState) =
    match action with
    | RoutedMouseAction.TogglePivot ->
        state.latched_mouse_navigation <- MouseNavigationMode.toggle PivotNavigation state.latched_mouse_navigation

        InputEffect.none
    | RoutedMouseAction.TogglePan ->
        state.latched_mouse_navigation <- MouseNavigationMode.toggle PanNavigation state.latched_mouse_navigation

        InputEffect.none
    | RoutedMouseAction.HoldPivot
    | RoutedMouseAction.HoldPan ->
        set_mouse_hold buttonBit true action state
        InputEffect.none
    | RoutedMouseAction.Retarget mode ->
        FlightCamera.apply_retarget_request RetargetScope.AllViews mode state
        |> InputEffect.barrier
    | RoutedMouseAction.Off -> InputEffect.none

let apply_mouse_action_up (buttonBit: int) (action: RoutedMouseAction) (state: FlyState) =
    set_mouse_hold buttonBit false action state

let apply_raw_mouse_button_transition (transition: RawMouseButtonTransition) (state: FlyState) =
    let keyboardActions =
        PlatformFlightKeyboard.apply_raw_mouse_button_transition transition

    let mutable effect = apply_keyboard_actions keyboardActions state

    if FlyState.is_running state then
        let mouse = state.config.mouse

        match transition.event with
        | RawMouseButtonEvent.MiddleDown ->
            effect <- InputEffect.combine effect (apply_mouse_action_down MIDDLE_BUTTON_BIT mouse.middle_button state)
        | RawMouseButtonEvent.MiddleUp -> apply_mouse_action_up MIDDLE_BUTTON_BIT mouse.middle_button state
        | RawMouseButtonEvent.Mouse4Down ->
            effect <- InputEffect.combine effect (apply_mouse_action_down MOUSE4_BUTTON_BIT mouse.mouse4 state)
        | RawMouseButtonEvent.Mouse4Up -> apply_mouse_action_up MOUSE4_BUTTON_BIT mouse.mouse4 state
        | RawMouseButtonEvent.Mouse5Down ->
            effect <- InputEffect.combine effect (apply_mouse_action_down MOUSE5_BUTTON_BIT mouse.mouse5 state)
        | RawMouseButtonEvent.Mouse5Up -> apply_mouse_action_up MOUSE5_BUTTON_BIT mouse.mouse5 state
        | RawMouseButtonEvent.LeftUp when mouse.exit_on_left -> FlyState.request_exit (explicit_exit_reason state) state
        | RawMouseButtonEvent.RightUp when state.session_mode.lifetime = FlightLifetime.WhileRightMouseHeld ->
            FlyState.request_exit RightMouseReleased state
        | RawMouseButtonEvent.RightUp when mouse.exit_on_right ->
            FlyState.request_exit (explicit_exit_reason state) state
        | RawMouseButtonEvent.None
        | RawMouseButtonEvent.LeftDown
        | RawMouseButtonEvent.LeftUp
        | RawMouseButtonEvent.RightDown
        | RawMouseButtonEvent.RightUp -> ()
        | _ -> ()

    if FlyState.is_running state then
        effect
    else
        { effect with pointer_barrier = true }

let apply_wheel_delta (wheelDelta: int64) (state: FlyState) =
    let wheel = state.wheel_remainder + wheelDelta

    if wheel = 0L then
        ViewChange.none
    else
        let wheelSteps = wheel / PlatformInput.wheel_delta
        state.wheel_remainder <- wheel - wheelSteps * PlatformInput.wheel_delta

        let direction =
            match state.config.movement.wheel_speed_mode with
            | MouseWheelSpeedMode.Off -> 0L
            | MouseWheelSpeedMode.Normal -> 1L
            | MouseWheelSpeedMode.Reversed -> -1L
            | _ -> 0L

        let changeSpeed =
            if direction = 0L then
                false
            else
                match state.active_mouse_navigation with
                | MouseLook -> true
                | MousePivot _
                | MousePan _ -> state.config.movement.wheel_changes_speed_during_flight_navigation

        if changeSpeed then
            if wheelSteps <> 0L then
                speed_steps state (direction * wheelSteps)

            ViewChange.none
        else
            FlightCamera.apply_navigation_wheel wheelSteps state

let update_state (now: float) (input: InputAccumulator.State) (state: FlyState) =
    let periodicValidationDue = now >= state.next_host_validation_at

    if periodicValidationDue then
        state.next_host_validation_at <- now + HOST_VALIDATION_INTERVAL_SECONDS
        PlatformFlightKeyboard.reconcile_physical_keys ()

    let exitReason =
        match InputAccumulator.exit_reason input with
        | Some reason ->
            match reason with
            | ExplicitKeepCamera when state.restore_camera_on_exit -> Some ExplicitRestoreCamera
            | _ -> Some reason
        | None ->
            if not (PlatformInput.viewport_host_windows_exist state.host_identity) then
                Some HostInvalid
            elif not (PlatformInput.viewport_id_matches state.host_identity state.view) then
                Some HostInvalid
            elif PlatformInput.foreground_root_window () <> state.host_identity.root_window then
                Some FocusLost
            else if periodicValidationDue then
                if PlatformInput.viewport_host_is_active state.host_identity state.view then
                    None
                else
                    Some HostInvalid
            else
                None

    match exitReason with
    | Some reason -> FlyState.request_exit reason state
    | None -> ()

let read_movement (state: FlyState) =
    let bindings = state.config.bindings
    let movement = state.config.movement

    let slowActive =
        if movement.slow_mode = KeyActivationMode.Hold then
            PlatformFlightKeyboard.binding_is_down bindings.slow
        else
            state.slow_enabled

    let boostActive =
        if movement.boost_mode = KeyActivationMode.Hold then
            PlatformFlightKeyboard.binding_is_down bindings.boost
        else
            state.boost_enabled

    let slow = if slowActive then movement.slow_multiplier else 1.
    let boost = if boostActive then movement.boost_multiplier else 1.

    { forward = PlatformFlightKeyboard.binding_is_down bindings.forward
      backward = PlatformFlightKeyboard.binding_is_down bindings.backward
      left = PlatformFlightKeyboard.binding_is_down bindings.left
      right = PlatformFlightKeyboard.binding_is_down bindings.right
      up = PlatformFlightKeyboard.binding_is_down bindings.up
      down = PlatformFlightKeyboard.binding_is_down bindings.down
      key_pivot_left = PlatformFlightKeyboard.binding_is_down bindings.key_pivot_left
      key_pivot_right = PlatformFlightKeyboard.binding_is_down bindings.key_pivot_right
      move_speed = state.speed * slow * boost }
