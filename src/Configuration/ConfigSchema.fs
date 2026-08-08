module RhinosCanFly.ConfigSchema

open System

[<Literal>]
let current_version = 0

let defaults: FlyConfigFile =
    { config_version = current_version
      forward = "W"
      backward = "S"
      left = "A"
      right = "D"
      up = "Q"
      down = "E"
      pivot_left = "Z"
      pivot_right = "X"
      boost_toggle = "LeftShift"
      slow = "LeftAlt"
      speed_increase = "Equals"
      speed_decrease = "Minus"
      exit_key = "Escape"
      base_speed = 36.
      minimum_speed = 1.
      maximum_speed = 100000.
      speed_step_multiplier = 1.2
      boost_multiplier = 3.
      slow_multiplier = 0.4
      pivot_speed_multiplier = 2.
      mouse_sensitivity = 15.
      invert_mouse_x = false
      invert_mouse_y = false
      normalize_diagonal_movement = true
      hide_gumball_while_flying = false
      pivot_bindings_ignore_gumball = false
      save_speed_to_document = true
      load_speed_from_document = true
      wheel_changes_speed = true
      exit_on_mouse_left = false
      exit_on_mouse_right = true
      exit_on_mouse_middle = false
      hijack_right_click_to_enter = true
      hijack_right_click_during_commands = true
      commands_do_not_repeat = true
      mouse_button_overrides_enabled = false
      mouse4_acts_as_middle = false
      mouse5_acts_as_middle = false
      shift_right_click_toggles_view = false
      alt_right_click_toggles_view = false
      shift_right_click_pans = false
      alt_right_click_pans = false
      boost_hold_instead_of_toggle = false
      slow_hold_instead_of_toggle = false
      vertical_speed_multiplier = 0.8
      lens_length_mm_in_mode = 0.
      viewport_redraw_mode = "rhino" }

let normalize_number (value: float) =
    Math.Round(value, 12, MidpointRounding.AwayFromZero)

let normalize_numbers (source: FlyConfigFile) =
    { source with
        base_speed = normalize_number source.base_speed
        minimum_speed = normalize_number source.minimum_speed
        maximum_speed = normalize_number source.maximum_speed
        speed_step_multiplier = normalize_number source.speed_step_multiplier
        boost_multiplier = normalize_number source.boost_multiplier
        slow_multiplier = normalize_number source.slow_multiplier
        pivot_speed_multiplier = normalize_number source.pivot_speed_multiplier
        mouse_sensitivity = normalize_number source.mouse_sensitivity
        vertical_speed_multiplier = normalize_number source.vertical_speed_multiplier
        lens_length_mm_in_mode = normalize_number source.lens_length_mm_in_mode }

let parse_viewport_redraw_mode (value: string) =
    match value.Trim().ToLowerInvariant() with
    | "rhino" -> Ok ViewportRedrawMode.Rhino
    | "rhino_immediate" -> Ok ViewportRedrawMode.RhinoImmediate
    | "native_window" -> Ok ViewportRedrawMode.NativeWindow
    | _ -> Error "viewport_redraw_mode must be rhino, rhino_immediate, or native_window"

let viewport_redraw_mode_value (mode: ViewportRedrawMode) =
    match mode with
    | ViewportRedrawMode.Rhino -> "rhino"
    | ViewportRedrawMode.RhinoImmediate -> "rhino_immediate"
    | ViewportRedrawMode.NativeWindow -> "native_window"

let compile (source: FlyConfigFile) =
    let errors = ResizeArray<string>()

    let required (name: string) (value: string) =
        match PlatformBindings.parse value with
        | Ok key -> key
        | Error error ->
            errors.Add $"{name}: {error}"

            { virtual_keys = [] }

    let optional (name: string) (value: string) =
        if String.IsNullOrWhiteSpace value then
            None
        else
            Some(required name value)

    let viewportRedrawMode =
        match parse_viewport_redraw_mode source.viewport_redraw_mode with
        | Ok mode -> mode
        | Error error ->
            errors.Add error
            ViewportRedrawMode.Rhino

    let positive (name: string) (value: float) =
        if Double.IsNaN value || Double.IsInfinity value || value <= 0. then
            errors.Add $"{name} must be a positive finite number"

    [ "base_speed", source.base_speed
      "minimum_speed", source.minimum_speed
      "maximum_speed", source.maximum_speed
      "speed_step_multiplier", source.speed_step_multiplier
      "boost_multiplier", source.boost_multiplier
      "slow_multiplier", source.slow_multiplier
      "pivot_speed_multiplier", source.pivot_speed_multiplier
      "mouse_sensitivity", source.mouse_sensitivity
      "vertical_speed_multiplier", source.vertical_speed_multiplier ]
    |> List.iter (fun (name: string, value: float) -> positive name value)

    if source.maximum_speed < source.minimum_speed then
        errors.Add "maximum_speed must be greater than or equal to minimum_speed"

    if
        source.base_speed < source.minimum_speed
        || source.base_speed > source.maximum_speed
    then
        errors.Add "base_speed must be between minimum_speed and maximum_speed"

    if source.speed_step_multiplier <= 1. then
        errors.Add "speed_step_multiplier must be greater than 1"

    if
        Double.IsNaN source.lens_length_mm_in_mode
        || Double.IsInfinity source.lens_length_mm_in_mode
        || source.lens_length_mm_in_mode < 0.
    then
        errors.Add "lens_length_mm_in_mode must be 0 (disabled) or a positive finite number"

    let config =
        { forward = required "forward" source.forward
          backward = required "backward" source.backward
          left = required "left" source.left
          right = required "right" source.right
          up = required "up" source.up
          down = required "down" source.down
          pivot_left = required "pivot_left" source.pivot_left
          pivot_right = required "pivot_right" source.pivot_right
          boost_toggle = required "boost_toggle" source.boost_toggle
          slow = required "slow" source.slow
          speed_increase = optional "speed_increase" source.speed_increase
          speed_decrease = optional "speed_decrease" source.speed_decrease
          exit_key = required "exit_key" source.exit_key
          base_speed = source.base_speed
          minimum_speed = source.minimum_speed
          maximum_speed = source.maximum_speed
          speed_step_multiplier = source.speed_step_multiplier
          boost_multiplier = source.boost_multiplier
          slow_multiplier = source.slow_multiplier
          pivot_speed_multiplier = source.pivot_speed_multiplier
          mouse_sensitivity =
            source.mouse_sensitivity
            |> ConfigMouseSensitivity
            |> MouseSensitivity.to_runtime
          invert_mouse_x = source.invert_mouse_x
          invert_mouse_y = source.invert_mouse_y
          normalize_diagonal_movement = source.normalize_diagonal_movement
          hide_gumball_while_flying = source.hide_gumball_while_flying
          pivot_bindings_ignore_gumball = source.pivot_bindings_ignore_gumball
          save_speed_to_document = source.save_speed_to_document
          load_speed_from_document = source.load_speed_from_document
          wheel_changes_speed = source.wheel_changes_speed
          exit_on_mouse_left = source.exit_on_mouse_left
          exit_on_mouse_right = source.exit_on_mouse_right
          exit_on_mouse_middle = source.exit_on_mouse_middle
          boost_hold_instead_of_toggle = source.boost_hold_instead_of_toggle
          slow_hold_instead_of_toggle = source.slow_hold_instead_of_toggle
          vertical_speed_multiplier = source.vertical_speed_multiplier
          lens_length_mm_in_mode = source.lens_length_mm_in_mode
          viewport_redraw_mode = viewportRedrawMode }

    if errors.Count = 0 then
        Ok config
    else
        Error(String.Join(Environment.NewLine, errors))
