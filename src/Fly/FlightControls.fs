module RhinosCanFly.FlightControls

type MouseExitState =
    { mutable left_was_down: bool
      mutable right_was_down: bool
      mutable middle_was_down: bool }

let is_down (key: KeyBinding) = PlatformBindings.is_down key

let is_optional_down (key: KeyBinding option) =
    key |> Option.map is_down |> Option.defaultValue false

let speed_step (state: FlyState) (direction: float) =
    state.speed <- FlightSpeed.step state.config state.speed direction

let update_toggles (state: FlyState) =
    let boost = is_down state.config.boost_toggle

    if
        not state.config.boost_hold_instead_of_toggle
        && boost
        && not state.boost_was_down
    then
        state.boost_enabled <- not state.boost_enabled

    state.boost_was_down <- boost

    let slow = is_down state.config.slow

    if not state.config.slow_hold_instead_of_toggle && slow && not state.slow_was_down then
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
        if state.config.slow_hold_instead_of_toggle then
            is_down state.config.slow
        else
            state.slow_enabled

    let boostActive =
        if state.config.boost_hold_instead_of_toggle then
            is_down state.config.boost_toggle
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
      move_speed = state.speed * slow * boost }

let movement_active (input: InputSnapshot) =
    input.forward
    || input.backward
    || input.left
    || input.right
    || input.up
    || input.down

let create_mouse_exit_state () =
    let buttons = PlatformInput.sample_mouse_buttons ()

    { left_was_down = buttons.left.is_down
      right_was_down = buttons.right.is_down
      middle_was_down = buttons.middle.is_down }

let mouse_exit_requested (config: FlyConfig) (state: MouseExitState) =
    let buttons = PlatformInput.sample_mouse_buttons ()

    let pressed (enabled: bool) (previouslyDown: bool) (button: PlatformInput.MouseButtonSample) =
        enabled && (button.was_pressed || button.is_down && not previouslyDown)

    let requested =
        pressed config.exit_on_mouse_left state.left_was_down buttons.left
        || pressed config.exit_on_mouse_right state.right_was_down buttons.right
        || pressed config.exit_on_mouse_middle state.middle_was_down buttons.middle

    state.left_was_down <- buttons.left.is_down
    state.right_was_down <- buttons.right.is_down
    state.middle_was_down <- buttons.middle.is_down
    requested

let poll (input: InputAccumulator.State) (state: FlyState) (mouseExitState: MouseExitState) =
    if
        PlatformInput.foreground_window () <> state.root_window
        || is_down state.config.exit_key
        || InputAccumulator.exit_requested input
        || mouse_exit_requested state.config mouseExitState
    then
        state.running <- false
        None
    else
        if state.config.wheel_changes_speed then
            let wheel = InputAccumulator.drain_wheel input

            if wheel <> 0 then
                speed_step state (float wheel / float PlatformInput.wheel_delta)

        update_toggles state
        Some(read_movement state)
