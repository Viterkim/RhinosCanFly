module RhinosCanFly.ConfigSchema

open System
open System.Globalization

[<Literal>]
let CURRENT_VERSION = 6

let default_parallel_view_flying () =
    { mode = ParallelViewFlyingMode.EnabledSome
      viewports = [| "Perspective" |] }

let defaults: FlyConfigFile =
    { config_version = CURRENT_VERSION
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
      toggle_projection = "H"
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
      wheel_changes_speed_during_flight_navigation = true
      exit_on_mouse_left = false
      exit_on_mouse_right = true
      middle_mouse_while_flying = FlyingMiddleMouseMode.Off
      mouse4_action_while_flying = false
      mouse5_action_while_flying = false
      right_click_entry_mode = RightClickEntryMode.ClickToFlyDuringCommands
      default_flight_mode = DefaultFlightMode.Normal
      shift_right_click_retarget = RetargetMode.ObjectCenter
      alt_right_click_retarget = RetargetMode.ObjectCenter
      ctrl_right_click_retarget = RetargetMode.ObjectCenter
      mouse4_retarget = RetargetMode.ObjectCenter
      mouse5_retarget = RetargetMode.ObjectCenter
      retarget_on_pivot = RetargetMode.ObjectCenter
      retarget_on_pan = RetargetMode.ObjectCenter
      retarget_on_flight_exit = RetargetMode.ObjectCenter
      retarget_on_restored_flight_exit = RetargetMode.Off
      perspective_retarget_fallback_multiplier = 0.9
      parallel_retarget_fallback_multiplier = 0.6
      perspective_retarget_zoom_border = 1.6
      parallel_retarget_zoom_border = 2.
      commands_do_not_repeat = true
      mouse4_action = MouseGestureAction.Off
      mouse5_action = MouseGestureAction.Off
      shift_right_click_action = MouseGestureAction.Off
      alt_right_click_action = MouseGestureAction.Off
      ctrl_right_click_action = MouseGestureAction.Off
      boost_mode = KeyActivationMode.Toggle
      slow_mode = KeyActivationMode.Toggle
      vertical_speed_multiplier = 0.7
      parallel_view_flying = default_parallel_view_flying ()
      parallel_mouse_sensitivity = 20.
      parallel_mouse_pivot_multiplier = 2.
      parallel_mouse_pan_multiplier = 2.
      parallel_zoom_speed_multiplier = 1.
      parallel_up_down_multiplier = 0.7
      perspective_lens_length_after_parallel_mm = 18.
      forced_perspective_lens_length_on_flight_start_mm = 0.
      perspective_lens_length_delta_during_flight_mm = 0.
      viewport_paint_mode = ViewportPaintMode.Queued }

let normalize_number (value: float) =
    let rounded = Math.Round(value, 12, MidpointRounding.AwayFromZero)

    if rounded = 0. then 0. else rounded

let format_number (value: float) =
    let normalized = normalize_number value
    normalized.ToString("0.############", CultureInfo.InvariantCulture)

let normalize_parallel_view_flying (source: ParallelViewFlyingFile) =
    if isNull (box source) then
        default_parallel_view_flying ()
    else
        let mode =
            match source.mode with
            | ParallelViewFlyingMode.DisabledAll
            | ParallelViewFlyingMode.EnabledAll
            | ParallelViewFlyingMode.EnabledSome
            | ParallelViewFlyingMode.DisabledSome -> source.mode
            | _ -> (default_parallel_view_flying ()).mode

        let viewports =
            if isNull source.viewports then
                Array.empty
            else
                source.viewports
                |> Array.map (fun (value: string) -> if isNull value then "" else value.Trim())
                |> Array.filter (String.IsNullOrWhiteSpace >> not)
                |> Array.distinctBy (fun (value: string) -> value.ToUpperInvariant())

        { mode = mode; viewports = viewports }

let normalize (source: FlyConfigFile) =
    { source with
        parallel_view_flying = normalize_parallel_view_flying source.parallel_view_flying
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
        perspective_retarget_fallback_multiplier = normalize_number source.perspective_retarget_fallback_multiplier
        parallel_retarget_fallback_multiplier = normalize_number source.parallel_retarget_fallback_multiplier
        perspective_retarget_zoom_border = normalize_number source.perspective_retarget_zoom_border
        parallel_retarget_zoom_border = normalize_number source.parallel_retarget_zoom_border
        vertical_speed_multiplier = normalize_number source.vertical_speed_multiplier
        parallel_mouse_sensitivity = normalize_number source.parallel_mouse_sensitivity
        parallel_mouse_pivot_multiplier = normalize_number source.parallel_mouse_pivot_multiplier
        parallel_mouse_pan_multiplier = normalize_number source.parallel_mouse_pan_multiplier
        parallel_zoom_speed_multiplier = normalize_number source.parallel_zoom_speed_multiplier
        parallel_up_down_multiplier = normalize_number source.parallel_up_down_multiplier
        perspective_lens_length_after_parallel_mm = normalize_number source.perspective_lens_length_after_parallel_mm
        forced_perspective_lens_length_on_flight_start_mm =
            normalize_number source.forced_perspective_lens_length_on_flight_start_mm
        perspective_lens_length_delta_during_flight_mm =
            normalize_number source.perspective_lens_length_delta_during_flight_mm }

let compile (source: FlyConfigFile) =
    let errors = ResizeArray<string>()

    let while_flying (enabled: bool) (action: MouseGestureAction) (retargetMode: RetargetMode) =
        if enabled then
            RoutedMouseAction.create action retargetMode
        else
            RoutedMouseAction.Off

    let parallelViewFlying =
        let sourceValue = source.parallel_view_flying

        let viewports =
            if isNull (box sourceValue) || isNull sourceValue.viewports then
                Array.empty
            else
                sourceValue.viewports
                |> Array.map (fun (value: string) -> if isNull value then "" else value.Trim())
                |> Array.filter (String.IsNullOrWhiteSpace >> not)
                |> Array.distinctBy (fun (value: string) -> value.ToUpperInvariant())

        if isNull (box sourceValue) then
            errors.Add "parallel_view_flying is missing"
            ParallelViewFlying.DisabledAll
        else
            match sourceValue.mode with
            | ParallelViewFlyingMode.EnabledAll -> ParallelViewFlying.EnabledAll
            | ParallelViewFlyingMode.EnabledSome -> ParallelViewFlying.EnabledSome viewports
            | ParallelViewFlyingMode.DisabledSome -> ParallelViewFlying.DisabledSome viewports
            | ParallelViewFlyingMode.DisabledAll -> ParallelViewFlying.DisabledAll
            | _ ->
                errors.Add "parallel_view_flying.mode is invalid"
                ParallelViewFlying.DisabledAll

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
      "perspective_retarget_fallback_multiplier", source.perspective_retarget_fallback_multiplier
      "parallel_retarget_fallback_multiplier", source.parallel_retarget_fallback_multiplier
      "perspective_retarget_zoom_border", source.perspective_retarget_zoom_border
      "parallel_retarget_zoom_border", source.parallel_retarget_zoom_border
      "vertical_speed_multiplier", source.vertical_speed_multiplier
      "parallel_mouse_sensitivity", source.parallel_mouse_sensitivity
      "parallel_mouse_pivot_multiplier", source.parallel_mouse_pivot_multiplier
      "parallel_mouse_pan_multiplier", source.parallel_mouse_pan_multiplier
      "parallel_zoom_speed_multiplier", source.parallel_zoom_speed_multiplier
      "parallel_up_down_multiplier", source.parallel_up_down_multiplier
      "perspective_lens_length_after_parallel_mm", source.perspective_lens_length_after_parallel_mm ]
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
        Double.IsNaN source.forced_perspective_lens_length_on_flight_start_mm
        || Double.IsInfinity source.forced_perspective_lens_length_on_flight_start_mm
        || source.forced_perspective_lens_length_on_flight_start_mm < 0.
    then
        errors.Add "forced_perspective_lens_length_on_flight_start_mm must be 0 (disabled) or a positive finite number"

    if
        Double.IsNaN source.perspective_lens_length_delta_during_flight_mm
        || Double.IsInfinity source.perspective_lens_length_delta_during_flight_mm
    then
        errors.Add "perspective_lens_length_delta_during_flight_mm must be a finite number"

    if source.forced_perspective_lens_length_on_flight_start_mm > 0. then
        let adjustedLens =
            source.forced_perspective_lens_length_on_flight_start_mm
            + source.perspective_lens_length_delta_during_flight_mm

        if
            Double.IsNaN adjustedLens
            || Double.IsInfinity adjustedLens
            || adjustedLens <= 0.
        then
            errors.Add
                "forced_perspective_lens_length_on_flight_start_mm plus perspective_lens_length_delta_during_flight_mm must be a positive finite number"

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
          "maximum parallel up/down speed",
          source.maximum_speed
          * maximumMovementMultiplier
          * source.parallel_up_down_multiplier
          "maximum parallel zoom speed",
          source.maximum_speed
          * maximumMovementMultiplier
          * source.parallel_zoom_speed_multiplier
          "maximum parallel mouse sensitivity",
          source.parallel_mouse_sensitivity
          * max 1. (max source.parallel_mouse_pivot_multiplier source.parallel_mouse_pan_multiplier)
          "key pivot angular speed", source.key_pivot_speed_multiplier * Math.PI / 6.
          "mouse pivot sensitivity", source.mouse_sensitivity * source.mouse_pivot_multiplier
          "mouse pan sensitivity", source.mouse_sensitivity * source.mouse_pan_multiplier
          "maximum perspective retarget fallback distance",
          source.maximum_speed * source.perspective_retarget_fallback_multiplier
          "maximum parallel retarget fallback distance",
          source.maximum_speed * source.parallel_retarget_fallback_multiplier ]

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
              cancel_flight_and_restore = required "cancel_flight_and_restore" source.cancel_flight_and_restore
              toggle_projection =
                if ParallelViewFlying.has_allowed_viewports parallelViewFlying then
                    optional "toggle_projection" source.toggle_projection
                else
                    None }
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
              wheel_changes_speed_during_flight_navigation = source.wheel_changes_speed_during_flight_navigation
              boost_mode = source.boost_mode
              slow_mode = source.slow_mode
              parallel_view =
                { flying = parallelViewFlying
                  mouse_sensitivity =
                    source.parallel_mouse_sensitivity
                    |> ConfigMouseSensitivity
                    |> MouseSensitivity.to_runtime
                  mouse_pivot_multiplier = MousePivotMultiplier source.parallel_mouse_pivot_multiplier
                  mouse_pan_multiplier = MousePanMultiplier source.parallel_mouse_pan_multiplier
                  zoom_speed_multiplier = source.parallel_zoom_speed_multiplier
                  up_down_multiplier = source.parallel_up_down_multiplier } }
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
              mouse4 = while_flying source.mouse4_action_while_flying source.mouse4_action source.mouse4_retarget
              mouse5 = while_flying source.mouse5_action_while_flying source.mouse5_action source.mouse5_retarget }
          behavior =
            { hide_gumball = source.hide_gumball_while_flying
              flight_pivot_uses_gumball = source.flight_pivot_uses_gumball
              retarget =
                { shift_right_click = source.shift_right_click_retarget
                  alt_right_click = source.alt_right_click_retarget
                  ctrl_right_click = source.ctrl_right_click_retarget
                  mouse4 = source.mouse4_retarget
                  mouse5 = source.mouse5_retarget
                  on_pivot = source.retarget_on_pivot
                  on_pan = source.retarget_on_pan
                  on_flight_exit = source.retarget_on_flight_exit
                  on_restored_flight_exit = source.retarget_on_restored_flight_exit
                  perspective_fallback_multiplier =
                    RetargetFallbackMultiplier source.perspective_retarget_fallback_multiplier
                  parallel_fallback_multiplier = RetargetFallbackMultiplier source.parallel_retarget_fallback_multiplier
                  perspective_zoom_border = source.perspective_retarget_zoom_border
                  parallel_zoom_border = source.parallel_retarget_zoom_border }
              save_speed_to_document = source.save_speed_to_document
              load_speed_from_document = source.load_speed_from_document
              perspective_lens =
                { after_parallel_mm = source.perspective_lens_length_after_parallel_mm
                  forced_on_flight_start_mm =
                    if source.forced_perspective_lens_length_on_flight_start_mm = 0. then
                        None
                    else
                        Some source.forced_perspective_lens_length_on_flight_start_mm
                  delta_during_flight_mm = source.perspective_lens_length_delta_during_flight_mm }
              viewport_paint_mode = source.viewport_paint_mode } }

    if errors.Count = 0 then
        Ok config
    else
        Error(String.Join(Environment.NewLine, errors))
