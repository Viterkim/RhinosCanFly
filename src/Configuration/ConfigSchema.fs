module RhinosCanFly.ConfigSchema

open System
open System.Globalization

[<Literal>]
let CURRENT_VERSION = 7

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
      middle_mouse_action_while_flying = false
      mouse4_action_while_flying = false
      mouse5_action_while_flying = false
      right_click_entry_mode = RightClickEntryMode.ClickToFlyDuringCommands
      default_flight_mode = DefaultFlightMode.Normal
      shift_right_click_retarget = RetargetMode.ObjectCenter
      alt_right_click_retarget = RetargetMode.ObjectCenter
      ctrl_right_click_retarget = RetargetMode.ObjectCenter
      middle_mouse_retarget = RetargetMode.ObjectCenter
      mouse4_retarget = RetargetMode.ObjectCenter
      mouse5_retarget = RetargetMode.ObjectCenter
      middle_mouse_uses_cursor_outside_flight = true
      mouse4_uses_cursor_outside_flight = true
      mouse5_uses_cursor_outside_flight = true
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
      middle_mouse_action = MouseGestureAction.Off
      shift_right_click_action = MouseGestureAction.Off
      alt_right_click_action = MouseGestureAction.Off
      ctrl_right_click_action = MouseGestureAction.Off
      boost_mode = KeyActivationMode.Toggle
      slow_mode = KeyActivationMode.Toggle
      vertical_speed_multiplier = 0.7
      parallel_view_flying = default_parallel_view_flying ()
      right_click_enters_parallel_views = true
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
