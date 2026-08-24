module RhinosCanFly.FlightControls

[<Literal>]
let HOST_VALIDATION_INTERVAL_SECONDS = 0.1

let is_optional_down (key: KeyBinding option) =
    match key with
    | Some binding -> PlatformInput.flight_binding_down binding
    | None -> false

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

let update_keyboard_navigation_input (state: FlyState) =
    let bindings = state.config.bindings.mouse_navigation
    let pivotToggle = is_optional_down bindings.pivot.toggle

    if pivotToggle && not state.keyboard_pivot_toggle_was_down then
        state.latched_mouse_navigation <- MouseNavigationMode.toggle PivotNavigation state.latched_mouse_navigation

    state.keyboard_pivot_toggle_was_down <- pivotToggle

    let panToggle = is_optional_down bindings.pan.toggle

    if panToggle && not state.keyboard_pan_toggle_was_down then
        state.latched_mouse_navigation <- MouseNavigationMode.toggle PanNavigation state.latched_mouse_navigation

    state.keyboard_pan_toggle_was_down <- panToggle

    state.keyboard_held_mouse_navigation <-
        if is_optional_down bindings.pan.hold then
            PanNavigation
        elif is_optional_down bindings.pivot.hold then
            PivotNavigation
        else
            LookNavigation

let update_toggles (state: FlyState) =
    let bindings = state.config.bindings
    let movement = state.config.movement
    let boost = PlatformInput.flight_binding_down bindings.boost

    if
        movement.boost_mode = KeyActivationMode.Toggle
        && boost
        && not state.boost_was_down
    then
        state.boost_enabled <- not state.boost_enabled

    state.boost_was_down <- boost

    let slow = PlatformInput.flight_binding_down bindings.slow

    if movement.slow_mode = KeyActivationMode.Toggle && slow && not state.slow_was_down then
        state.slow_enabled <- not state.slow_enabled

    state.slow_was_down <- slow

    let increase = is_optional_down bindings.speed_increase

    if increase && not state.speed_increase_was_down then
        speed_step state (SpeedStepCount 1.)

    state.speed_increase_was_down <- increase

    let decrease = is_optional_down bindings.speed_decrease

    if decrease && not state.speed_decrease_was_down then
        speed_step state (SpeedStepCount -1.)

    state.speed_decrease_was_down <- decrease

    let toggleProjection = is_optional_down bindings.toggle_projection

    if toggleProjection && not state.keyboard_projection_toggle_was_down then
        FlightCamera.toggle_projection state

    state.keyboard_projection_toggle_was_down <- toggleProjection

let read_movement (state: FlyState) =
    let bindings = state.config.bindings
    let movement = state.config.movement

    let slowActive =
        if movement.slow_mode = KeyActivationMode.Hold then
            state.slow_was_down
        else
            state.slow_enabled

    let boostActive =
        if movement.boost_mode = KeyActivationMode.Hold then
            state.boost_was_down
        else
            state.boost_enabled

    let slow = if slowActive then movement.slow_multiplier else 1.
    let boost = if boostActive then movement.boost_multiplier else 1.

    { forward = PlatformInput.flight_binding_down bindings.forward
      backward = PlatformInput.flight_binding_down bindings.backward
      left = PlatformInput.flight_binding_down bindings.left
      right = PlatformInput.flight_binding_down bindings.right
      up = PlatformInput.flight_binding_down bindings.up
      down = PlatformInput.flight_binding_down bindings.down
      key_pivot_left = PlatformInput.flight_binding_down bindings.key_pivot_left
      key_pivot_right = PlatformInput.flight_binding_down bindings.key_pivot_right
      move_speed = state.speed * slow * boost }

let reconcile_held_mouse_navigation (input: InputAccumulator.State) (state: FlyState) =
    let mouse = state.config.mouse

    let middlePivotConfigured = RoutedMouseAction.holds_pivot mouse.middle_button
    let mouse4PivotConfigured = RoutedMouseAction.holds_pivot mouse.mouse4
    let mouse5PivotConfigured = RoutedMouseAction.holds_pivot mouse.mouse5
    let middlePanConfigured = RoutedMouseAction.holds_pan mouse.middle_button
    let mouse4PanConfigured = RoutedMouseAction.holds_pan mouse.mouse4
    let mouse5PanConfigured = RoutedMouseAction.holds_pan mouse.mouse5

    let middleHeld = PlatformInput.middle_mouse_button_down ()
    let mouse4Held = PlatformInput.mouse4_button_down ()
    let mouse5Held = PlatformInput.mouse5_button_down ()

    if middlePivotConfigured || mouse4PivotConfigured || mouse5PivotConfigured then
        InputAccumulator.set_pivot_held
            ((middlePivotConfigured && middleHeld)
             || (mouse4PivotConfigured && mouse4Held)
             || (mouse5PivotConfigured && mouse5Held))
            input

    if middlePanConfigured || mouse4PanConfigured || mouse5PanConfigured then
        InputAccumulator.set_pan_held
            ((middlePanConfigured && middleHeld)
             || (mouse4PanConfigured && mouse4Held)
             || (mouse5PanConfigured && mouse5Held))
            input

let apply_wheel_input (input: InputAccumulator.State) (state: FlyState) =
    let wheel = state.wheel_remainder + InputAccumulator.drain_wheel input

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
    let cancelAndRestore =
        PlatformInput.flight_binding_down state.config.bindings.cancel_flight_and_restore

    let periodicValidationDue = now >= state.next_host_validation_at

    if periodicValidationDue then
        state.next_host_validation_at <- now + HOST_VALIDATION_INTERVAL_SECONDS

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
            elif
                periodicValidationDue
                && state.session_mode.lifetime = FlightLifetime.WhileRightMouseHeld
                && not (PlatformInput.right_mouse_button_down ())
            then
                Some RightMouseReleased
            elif cancelAndRestore then
                Some ExplicitRestoreCamera
            elif PlatformInput.flight_binding_down state.config.bindings.exit_key then
                if state.restore_camera_on_exit then
                    Some ExplicitRestoreCamera
                else
                    Some ExplicitKeepCamera
            else if periodicValidationDue then
                if PlatformInput.viewport_host_is_active state.host_identity state.view then
                    None
                else
                    Some HostInvalid
            else
                None

    match exitReason with
    | Some reason -> FlyState.request_exit reason state
    | None ->
        if periodicValidationDue then
            reconcile_held_mouse_navigation input state

        update_toggles state
