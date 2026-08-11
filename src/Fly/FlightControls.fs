module RhinosCanFly.FlightControls

let is_down (key: KeyBinding) = PlatformBindings.is_down key

let is_optional_down (key: KeyBinding option) =
    match key with
    | Some binding -> is_down binding
    | None -> false

let speed_step (state: FlyState) (direction: float) =
    state.speed <- FlightSpeed.step state.config.movement state.speed direction

let update_keyboard_navigation_input (state: FlyState) =
    let bindings = state.config.bindings.mouse_navigation
    let pivotToggle = is_optional_down bindings.pivot.toggle

    if pivotToggle && not state.keyboard_pivot_toggle_was_down then
        state.latched_mouse_navigation <- MouseNavigationKind.toggle PivotNavigation state.latched_mouse_navigation

    state.keyboard_pivot_toggle_was_down <- pivotToggle

    let panToggle = is_optional_down bindings.pan.toggle

    if panToggle && not state.keyboard_pan_toggle_was_down then
        state.latched_mouse_navigation <- MouseNavigationKind.toggle PanNavigation state.latched_mouse_navigation

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
    let boost = is_down bindings.boost

    if
        movement.boost_mode = KeyActivationMode.Toggle
        && boost
        && not state.boost_was_down
    then
        state.boost_enabled <- not state.boost_enabled

    state.boost_was_down <- boost

    let slow = is_down bindings.slow

    if movement.slow_mode = KeyActivationMode.Toggle && slow && not state.slow_was_down then
        state.slow_enabled <- not state.slow_enabled

    state.slow_was_down <- slow

    let increase = is_optional_down bindings.speed_increase

    if increase && not state.speed_increase_was_down then
        speed_step state 1.

    state.speed_increase_was_down <- increase

    let decrease = is_optional_down bindings.speed_decrease

    if decrease && not state.speed_decrease_was_down then
        speed_step state -1.

    state.speed_decrease_was_down <- decrease

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

    { forward = is_down bindings.forward
      backward = is_down bindings.backward
      left = is_down bindings.left
      right = is_down bindings.right
      up = is_down bindings.up
      down = is_down bindings.down
      pivot_left = is_down bindings.pivot_left
      pivot_right = is_down bindings.pivot_right
      move_speed = state.speed * slow * boost }

let update_state (input: InputAccumulator.State) (state: FlyState) =
    if
        PlatformInput.foreground_root_window () <> state.root_window
        || is_down state.config.bindings.exit_key
        || InputAccumulator.exit_requested input
    then
        state.running <- false
    else
        let wheel = InputAccumulator.drain_wheel input

        if wheel <> 0 then
            let direction =
                match state.config.movement.wheel_speed_mode with
                | MouseWheelSpeedMode.Off -> 0.
                | MouseWheelSpeedMode.Normal -> 1.
                | MouseWheelSpeedMode.Reversed -> -1.
                | _ -> 0.

            if direction <> 0. then
                speed_step state (direction * float wheel / float PlatformInput.wheel_delta)

        update_toggles state
