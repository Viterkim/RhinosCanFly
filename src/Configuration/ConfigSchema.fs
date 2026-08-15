module RhinosCanFly.ConfigSchema

open System
open System.Globalization

[<Literal>]
let current_version = 5

let defaults: FlyConfigFile =
    { config_version = current_version
      enabled = true
      forward = "W"
      backward = "S"
      left = "A"
      right = "D"
      up = "Q"
      down = "E"
      key_pivot_left = "Z"
      key_pivot_right = "X"
      pivot_toggle = "F"
      pivot_hold = "G"
      pan_toggle = "C"
      pan_hold = "V"
      boost = "LeftShift"
      slow = "LeftAlt"
      speed_increase = "Equals"
      speed_decrease = "Minus"
      exit_key = "Escape"
      cancel_flight_and_restore = "T"
      base_speed = 36.
      minimum_speed = 1.
      maximum_speed = 100000.
      speed_step_multiplier = 1.2
      boost_multiplier = 3.
      slow_multiplier = 0.4
      key_pivot_speed_multiplier = 10.
      mouse_pivot_multiplier = 4.
      mouse_pan_multiplier = 2.
      mouse_sensitivity = 15.
      mouse_x_mode = MouseAxisMode.Normal
      mouse_y_mode = MouseAxisMode.Normal
      normalize_diagonal_movement = true
      hide_gumball_while_flying = false
      flight_pivot_uses_gumball = true
      save_speed_to_document = true
      load_speed_from_document = true
      wheel_speed_mode = MouseWheelSpeedMode.Normal
      exit_on_mouse_left = false
      exit_on_mouse_right = true
      middle_mouse_while_flying = FlyingMiddleMouseMode.Off
      mouse4_pivot_in_flight = false
      mouse5_pivot_in_flight = false
      right_click_entry_mode = RightClickEntryMode.ClickToFlyDuringCommands
      default_flight_mode = DefaultFlightMode.Normal
      view_target_mode = ViewTargetMode.GeometryThenDistance
      view_target_distance_multiplier = 1.
      set_view_target_on_restored_flights = false
      commands_do_not_repeat = true
      mouse4_pivot_mode = MouseButtonPivotMode.Off
      mouse5_pivot_mode = MouseButtonPivotMode.Off
      shift_right_click_mode = ModifiedRightClickMode.Off
      alt_right_click_mode = ModifiedRightClickMode.Off
      boost_mode = KeyActivationMode.Toggle
      slow_mode = KeyActivationMode.Toggle
      vertical_speed_multiplier = 0.8
      forced_lens_length_mm = 0.
      lens_length_delta_mm = 0.
      viewport_paint_mode = ViewportPaintMode.Immediate }

let normalize_number (value: float) =
    let rounded = Math.Round(value, 12, MidpointRounding.AwayFromZero)

    if rounded = 0. then 0. else rounded

let format_number (value: float) =
    let normalized = normalize_number value
    normalized.ToString("0.############", CultureInfo.InvariantCulture)

let normalize_numbers (source: FlyConfigFile) =
    { source with
        base_speed = normalize_number source.base_speed
        minimum_speed = normalize_number source.minimum_speed
        maximum_speed = normalize_number source.maximum_speed
        speed_step_multiplier = normalize_number source.speed_step_multiplier
        boost_multiplier = normalize_number source.boost_multiplier
        slow_multiplier = normalize_number source.slow_multiplier
        key_pivot_speed_multiplier = normalize_number source.key_pivot_speed_multiplier
        mouse_pivot_multiplier = normalize_number source.mouse_pivot_multiplier
        mouse_pan_multiplier = normalize_number source.mouse_pan_multiplier
        mouse_sensitivity = normalize_number source.mouse_sensitivity
        view_target_distance_multiplier = normalize_number source.view_target_distance_multiplier
        vertical_speed_multiplier = normalize_number source.vertical_speed_multiplier
        forced_lens_length_mm = normalize_number source.forced_lens_length_mm
        lens_length_delta_mm = normalize_number source.lens_length_delta_mm }

let compile (source: FlyConfigFile) =
    let errors = ResizeArray<string>()

    let required (name: string) (value: string) =
        match PlatformBindings.parse value with
        | Ok key -> key
        | Error error ->
            errors.Add $"{name}: {error}"

            { virtual_keys = Array.empty<VirtualKey> }

    let optional (name: string) (value: string) =
        if String.IsNullOrWhiteSpace value then
            None
        else
            Some(required name value)

    let positive (name: string) (value: float) =
        if Double.IsNaN value || Double.IsInfinity value || value <= 0. then
            errors.Add $"{name} must be a positive finite number"

    [ "base_speed", source.base_speed
      "minimum_speed", source.minimum_speed
      "maximum_speed", source.maximum_speed
      "speed_step_multiplier", source.speed_step_multiplier
      "boost_multiplier", source.boost_multiplier
      "slow_multiplier", source.slow_multiplier
      "key_pivot_speed_multiplier", source.key_pivot_speed_multiplier
      "mouse_pivot_multiplier", source.mouse_pivot_multiplier
      "mouse_pan_multiplier", source.mouse_pan_multiplier
      "mouse_sensitivity", source.mouse_sensitivity
      "view_target_distance_multiplier", source.view_target_distance_multiplier
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
        Double.IsNaN source.forced_lens_length_mm
        || Double.IsInfinity source.forced_lens_length_mm
        || source.forced_lens_length_mm < 0.
    then
        errors.Add "forced_lens_length_mm must be 0 (disabled) or a positive finite number"

    if
        Double.IsNaN source.lens_length_delta_mm
        || Double.IsInfinity source.lens_length_delta_mm
    then
        errors.Add "lens_length_delta_mm must be a finite number"

    if source.forced_lens_length_mm > 0. then
        let adjustedLens = source.forced_lens_length_mm + source.lens_length_delta_mm

        if
            Double.IsNaN adjustedLens
            || Double.IsInfinity adjustedLens
            || adjustedLens <= 0.
        then
            errors.Add "forced_lens_length_mm plus lens_length_delta_mm must be a positive finite number"

    let derivedValues =
        let combinedMovementMultiplier = source.boost_multiplier * source.slow_multiplier

        let maximumMovementMultiplier =
            max 1. (max source.boost_multiplier (max source.slow_multiplier combinedMovementMultiplier))

        [ "combined boost and slow multiplier", combinedMovementMultiplier
          "maximum movement speed", source.maximum_speed * maximumMovementMultiplier
          "maximum vertical movement speed",
          source.maximum_speed
          * maximumMovementMultiplier
          * source.vertical_speed_multiplier
          "key pivot angular speed", source.key_pivot_speed_multiplier * Math.PI / 6.
          "mouse pivot sensitivity", source.mouse_sensitivity * source.mouse_pivot_multiplier
          "mouse pan sensitivity", source.mouse_sensitivity * source.mouse_pan_multiplier
          "maximum target distance", source.maximum_speed * source.view_target_distance_multiplier ]

    for name, value in derivedValues do
        if Double.IsNaN value || Double.IsInfinity value then
            errors.Add $"{name} is too large"

    let config: FlyConfig =
        { bindings =
            { forward = required "forward" source.forward
              backward = required "backward" source.backward
              left = required "left" source.left
              right = required "right" source.right
              up = required "up" source.up
              down = required "down" source.down
              key_pivot_left = required "key_pivot_left" source.key_pivot_left
              key_pivot_right = required "key_pivot_right" source.key_pivot_right
              mouse_navigation =
                { pivot =
                    { toggle = optional "pivot_toggle" source.pivot_toggle
                      hold = optional "pivot_hold" source.pivot_hold }
                  pan =
                    { toggle = optional "pan_toggle" source.pan_toggle
                      hold = optional "pan_hold" source.pan_hold } }
              boost = required "boost" source.boost
              slow = required "slow" source.slow
              speed_increase = optional "speed_increase" source.speed_increase
              speed_decrease = optional "speed_decrease" source.speed_decrease
              exit_key = required "exit_key" source.exit_key
              cancel_flight_and_restore = required "cancel_flight_and_restore" source.cancel_flight_and_restore }
          movement =
            { base_speed = source.base_speed
              speed_range =
                { minimum = source.minimum_speed
                  maximum = source.maximum_speed }
              speed_step_multiplier = source.speed_step_multiplier
              boost_multiplier = source.boost_multiplier
              slow_multiplier = source.slow_multiplier
              key_pivot_speed_multiplier = source.key_pivot_speed_multiplier
              vertical_speed_multiplier = source.vertical_speed_multiplier
              normalize_diagonal_movement = source.normalize_diagonal_movement
              wheel_speed_mode = source.wheel_speed_mode
              boost_mode = source.boost_mode
              slow_mode = source.slow_mode }
          mouse =
            { pivot_multiplier = MousePivotMultiplier source.mouse_pivot_multiplier
              pan_multiplier = MousePanMultiplier source.mouse_pan_multiplier
              sensitivity =
                source.mouse_sensitivity
                |> ConfigMouseSensitivity
                |> MouseSensitivity.to_runtime
              x_mode = source.mouse_x_mode
              y_mode = source.mouse_y_mode
              exit_on_left = source.exit_on_mouse_left
              exit_on_right = source.exit_on_mouse_right
              middle_button = source.middle_mouse_while_flying
              mouse4_pivot_in_flight = source.mouse4_pivot_in_flight
              mouse5_pivot_in_flight = source.mouse5_pivot_in_flight
              mouse4_pivot_mode = source.mouse4_pivot_mode
              mouse5_pivot_mode = source.mouse5_pivot_mode }
          behavior =
            { hide_gumball = source.hide_gumball_while_flying
              flight_pivot_uses_gumball = source.flight_pivot_uses_gumball
              view_target =
                { mode = source.view_target_mode
                  distance_multiplier = ViewTargetDistanceMultiplier source.view_target_distance_multiplier
                  set_on_restored_flights = source.set_view_target_on_restored_flights }
              save_speed_to_document = source.save_speed_to_document
              load_speed_from_document = source.load_speed_from_document
              lens_adjustment =
                { forced_length_mm =
                    if source.forced_lens_length_mm = 0. then
                        None
                    else
                        Some source.forced_lens_length_mm
                  delta_mm = source.lens_length_delta_mm }
              viewport_paint_mode = source.viewport_paint_mode } }

    if errors.Count = 0 then
        Ok config
    else
        Error(String.Join(Environment.NewLine, errors))
