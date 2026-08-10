module RhinosCanFly.FlightControls

let is_down (key: KeyBinding) = PlatformBindings.is_down key

let is_optional_down (key: KeyBinding option) =
    match key with
    | Some binding -> is_down binding
    | None -> false

let speed_step (state: FlyState) (direction: float) =
    state.speed <- FlightSpeed.step state.config state.speed direction

let update_keyboard_pivot_input (state: FlyState) =
    let toggle = is_optional_down state.config.pivot_toggle

    if toggle && not state.keyboard_pivot_toggle_was_down then
        state.pivot_latched <- not state.pivot_latched

    state.keyboard_pivot_toggle_was_down <- toggle
    state.keyboard_pivot_held <- is_optional_down state.config.pivot_hold

let update_toggles (state: FlyState) =
    let boost = is_down state.config.boost

    if
        state.config.boost_mode = KeyActivationMode.Toggle
        && boost
        && not state.boost_was_down
    then
        state.boost_enabled <- not state.boost_enabled

    state.boost_was_down <- boost

    let slow = is_down state.config.slow

    if
        state.config.slow_mode = KeyActivationMode.Toggle
        && slow
        && not state.slow_was_down
    then
        state.slow_enabled <- not state.slow_enabled

    state.slow_was_down <- slow

    let increase = is_optional_down state.config.speed_increase

    if increase && not state.speed_increase_was_down then
        speed_step state 1.

    state.speed_increase_was_down <- increase

    let decrease = is_optional_down state.config.speed_decrease

    if decrease && not state.speed_decrease_was_down then
        speed_step state -1.

    state.speed_decrease_was_down <- decrease

let read_movement (state: FlyState) =
    let slowActive =
        if state.config.slow_mode = KeyActivationMode.Hold then
            is_down state.config.slow
        else
            state.slow_enabled

    let boostActive =
        if state.config.boost_mode = KeyActivationMode.Hold then
            is_down state.config.boost
        else
            state.boost_enabled

    let slow = if slowActive then state.config.slow_multiplier else 1.
    let boost = if boostActive then state.config.boost_multiplier else 1.

    { forward = is_down state.config.forward
      backward = is_down state.config.backward
      left = is_down state.config.left
      right = is_down state.config.right
      up = is_down state.config.up
      down = is_down state.config.down
      pivot_left = is_down state.config.pivot_left
      pivot_right = is_down state.config.pivot_right
      move_speed = state.speed * slow * boost }

let update_state (input: InputAccumulator.State) (state: FlyState) =
    if
        PlatformInput.foreground_window () <> state.root_window
        || is_down state.config.exit_key
        || InputAccumulator.exit_requested input
    then
        state.running <- false
    else
        if state.config.wheel_changes_speed then
            let wheel = InputAccumulator.drain_wheel input

            if wheel <> 0 then
                speed_step state (float wheel / float PlatformInput.wheel_delta)

        update_toggles state
