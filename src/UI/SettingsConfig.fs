module RhinosCanFly.SettingsConfig

open System
open System.Globalization
open Eto.Forms

type NumberValues =
    { base_speed: float
      minimum_speed: float
      maximum_speed: float
      speed_step_multiplier: float
      boost_multiplier: float
      slow_multiplier: float
      vertical_speed_multiplier: float
      key_pivot_speed_multiplier: float
      mouse_pivot_multiplier: float
      mouse_pan_multiplier: float
      perspective_retarget_fallback_multiplier: float
      parallel_retarget_fallback_multiplier: float
      perspective_retarget_zoom_border: float
      parallel_retarget_zoom_border: float
      parallel_mouse_sensitivity: float
      parallel_mouse_pivot_multiplier: float
      parallel_mouse_pan_multiplier: float
      parallel_zoom_speed_multiplier: float
      parallel_up_down_multiplier: float
      mouse_sensitivity: float
      perspective_lens_length_after_parallel_mm: float
      forced_perspective_lens_length_on_flight_start_mm: float
      perspective_lens_length_delta_during_flight_mm: float }

let is_checked (control: CheckBox) = control.Checked.GetValueOrDefault()

let set_checked (control: CheckBox) (value: bool) = control.Checked <- Nullable value

let parse_number (name: string) (field: TextBox) =
    let mutable value = 0.
    let text = if isNull field.Text then "" else field.Text.Trim()

    if
        Double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, &value)
        || Double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, &value)
    then
        Ok value
    else
        Error $"{name} must be a number."

let parse_optional_number (name: string) (field: TextBox) =
    if String.IsNullOrWhiteSpace field.Text then
        Ok 0.
    else
        parse_number name field

let parse_numbers (fields: SettingsFields.NumberFields) =
    let errors = ResizeArray<string>()

    let required (name: string) (field: TextBox) =
        match parse_number name field with
        | Ok value -> value
        | Error error ->
            errors.Add error
            0.

    let optional (name: string) (field: TextBox) =
        match parse_optional_number name field with
        | Ok value -> value
        | Error error ->
            errors.Add error
            0.

    let values =
        { base_speed = required "Base speed" fields.base_speed
          minimum_speed = required "Minimum speed" fields.minimum_speed
          maximum_speed = required "Maximum speed" fields.maximum_speed
          speed_step_multiplier = required "Speed step multiplier" fields.speed_step_multiplier
          boost_multiplier = required "Boost multiplier" fields.boost_multiplier
          slow_multiplier = required "Slow multiplier" fields.slow_multiplier
          vertical_speed_multiplier = required "Move up/down multiplier" fields.vertical_speed_multiplier
          key_pivot_speed_multiplier = required "Key pivot speed multiplier" fields.key_pivot_speed_multiplier
          mouse_pivot_multiplier = required "Pivot multiplier" fields.mouse_pivot_multiplier
          mouse_pan_multiplier = required "Pan multiplier" fields.mouse_pan_multiplier
          perspective_retarget_fallback_multiplier =
            required "Perspective fallback multiplier" fields.perspective_retarget_fallback_multiplier
          parallel_retarget_fallback_multiplier =
            required "Parallel fallback multiplier" fields.parallel_retarget_fallback_multiplier
          perspective_retarget_zoom_border = required "Perspective zoom border" fields.perspective_retarget_zoom_border
          parallel_retarget_zoom_border = required "Parallel zoom border" fields.parallel_retarget_zoom_border
          parallel_mouse_sensitivity = required "Parallel sensitivity" fields.parallel_mouse_sensitivity
          parallel_mouse_pivot_multiplier = required "Parallel pivot multiplier" fields.parallel_mouse_pivot_multiplier
          parallel_mouse_pan_multiplier = required "Parallel pan multiplier" fields.parallel_mouse_pan_multiplier
          parallel_zoom_speed_multiplier = required "Parallel zoom speed" fields.parallel_zoom_speed_multiplier
          parallel_up_down_multiplier = required "Parallel up/down multiplier" fields.parallel_up_down_multiplier
          mouse_sensitivity = required "Mouse sensitivity" fields.mouse_sensitivity
          perspective_lens_length_after_parallel_mm =
            required "Perspective lens from parallel" fields.perspective_lens_length_after_parallel_mm
          forced_perspective_lens_length_on_flight_start_mm =
            optional "Forced perspective lens on flight start" fields.forced_perspective_lens_length_on_flight_start_mm
          perspective_lens_length_delta_during_flight_mm =
            optional "Perspective lens diff during flight" fields.perspective_lens_length_delta_during_flight_mm }

    if errors.Count = 0 then
        Ok values
    else
        Error(String.Join(Environment.NewLine, errors))

let load (fields: SettingsFields.ConfigFields) (config: FlyConfigFile) =
    let bindings = fields.bindings
    let numbers = fields.numbers
    let modes = fields.modes
    let options = fields.options

    set_checked options.enabled config.enabled
    bindings.forward.Text <- config.forward
    bindings.backward.Text <- config.backward
    bindings.left.Text <- config.left
    bindings.right.Text <- config.right
    bindings.up.Text <- config.up
    bindings.down.Text <- config.down
    bindings.key_pivot_left.Text <- config.key_pivot_left
    bindings.key_pivot_right.Text <- config.key_pivot_right
    bindings.pivot_toggle.Text <- config.pivot_toggle
    bindings.pivot_hold.Text <- config.pivot_hold
    bindings.pan_toggle.Text <- config.pan_toggle
    bindings.pan_hold.Text <- config.pan_hold
    bindings.boost.Text <- config.boost
    bindings.slow.Text <- config.slow
    bindings.speed_increase.Text <- config.speed_increase
    bindings.speed_decrease.Text <- config.speed_decrease
    bindings.retarget_all_views.Text <- config.retarget_all_views
    bindings.retarget_other_views.Text <- config.retarget_other_views
    bindings.untilt_view.Text <- config.untilt_view
    bindings.exit_key.Text <- config.exit_key
    bindings.cancel_flight_and_restore.Text <- config.cancel_flight_and_restore
    bindings.toggle_projection.Text <- config.toggle_projection
    numbers.base_speed.Text <- ConfigSchema.format_number config.base_speed
    numbers.minimum_speed.Text <- ConfigSchema.format_number config.minimum_speed
    numbers.maximum_speed.Text <- ConfigSchema.format_number config.maximum_speed
    numbers.speed_step_multiplier.Text <- ConfigSchema.format_number config.speed_step_multiplier
    numbers.boost_multiplier.Text <- ConfigSchema.format_number config.boost_multiplier
    numbers.slow_multiplier.Text <- ConfigSchema.format_number config.slow_multiplier
    numbers.vertical_speed_multiplier.Text <- ConfigSchema.format_number config.vertical_speed_multiplier
    numbers.key_pivot_speed_multiplier.Text <- ConfigSchema.format_number config.key_pivot_speed_multiplier
    numbers.mouse_pivot_multiplier.Text <- ConfigSchema.format_number config.mouse_pivot_multiplier
    numbers.mouse_pan_multiplier.Text <- ConfigSchema.format_number config.mouse_pan_multiplier

    numbers.perspective_retarget_fallback_multiplier.Text <-
        ConfigSchema.format_number config.perspective_retarget_fallback_multiplier

    numbers.parallel_retarget_fallback_multiplier.Text <-
        ConfigSchema.format_number config.parallel_retarget_fallback_multiplier

    numbers.perspective_retarget_zoom_border.Text <- ConfigSchema.format_number config.perspective_retarget_zoom_border

    numbers.parallel_retarget_zoom_border.Text <- ConfigSchema.format_number config.parallel_retarget_zoom_border

    numbers.parallel_mouse_sensitivity.Text <- ConfigSchema.format_number config.parallel_mouse_sensitivity
    numbers.parallel_mouse_pivot_multiplier.Text <- ConfigSchema.format_number config.parallel_mouse_pivot_multiplier
    numbers.parallel_mouse_pan_multiplier.Text <- ConfigSchema.format_number config.parallel_mouse_pan_multiplier

    numbers.parallel_zoom_speed_multiplier.Text <- ConfigSchema.format_number config.parallel_zoom_speed_multiplier
    numbers.parallel_up_down_multiplier.Text <- ConfigSchema.format_number config.parallel_up_down_multiplier

    numbers.mouse_sensitivity.Text <- ConfigSchema.format_number config.mouse_sensitivity

    numbers.perspective_lens_length_after_parallel_mm.Text <-
        ConfigSchema.format_number config.perspective_lens_length_after_parallel_mm

    numbers.forced_perspective_lens_length_on_flight_start_mm.Text <-
        ConfigSchema.format_number config.forced_perspective_lens_length_on_flight_start_mm

    numbers.perspective_lens_length_delta_during_flight_mm.Text <-
        ConfigSchema.format_number config.perspective_lens_length_delta_during_flight_mm

    SettingsFields.set_mode modes.viewport_paint_mode config.viewport_paint_mode
    SettingsFields.set_mode modes.boost_mode config.boost_mode
    SettingsFields.set_mode modes.slow_mode config.slow_mode
    SettingsFields.set_mode modes.wheel_speed_mode config.wheel_speed_mode
    SettingsFields.set_mode modes.right_click_entry_mode config.right_click_entry_mode
    SettingsFields.set_mode modes.default_flight_mode config.default_flight_mode
    SettingsFields.set_mode modes.prioritized_target config.prioritized_target
    SettingsFields.set_mode modes.shift_right_click_retarget config.shift_right_click_retarget
    SettingsFields.set_mode modes.alt_right_click_retarget config.alt_right_click_retarget
    SettingsFields.set_mode modes.ctrl_right_click_retarget config.ctrl_right_click_retarget
    SettingsFields.set_mode modes.middle_mouse_retarget config.middle_mouse_retarget
    SettingsFields.set_mode modes.mouse4_retarget config.mouse4_retarget
    SettingsFields.set_mode modes.mouse5_retarget config.mouse5_retarget
    SettingsFields.set_mode modes.retarget_all_views_mode config.retarget_all_views_mode
    SettingsFields.set_mode modes.retarget_other_views_mode config.retarget_other_views_mode
    SettingsFields.set_mode modes.retarget_on_pivot config.retarget_on_pivot
    SettingsFields.set_mode modes.retarget_on_pan config.retarget_on_pan
    SettingsFields.set_mode modes.retarget_on_flight_exit config.retarget_on_flight_exit
    SettingsFields.set_mode modes.retarget_on_restored_flight_exit config.retarget_on_restored_flight_exit
    SettingsFields.set_mode modes.shift_right_click_action config.shift_right_click_action
    SettingsFields.set_mode modes.alt_right_click_action config.alt_right_click_action
    SettingsFields.set_mode modes.ctrl_right_click_action config.ctrl_right_click_action
    SettingsFields.set_mode modes.mouse4_action config.mouse4_action
    SettingsFields.set_mode modes.mouse5_action config.mouse5_action
    SettingsFields.set_mode modes.middle_mouse_action config.middle_mouse_action
    SettingsFields.set_mode modes.mouse_x_mode config.mouse_x_mode
    SettingsFields.set_mode modes.mouse_y_mode config.mouse_y_mode
    SettingsFields.set_mode modes.viewport_capabilities config.viewport_capabilities.mode
    SettingsFields.set_mode modes.right_click_flight_entry config.right_click_flight_entry.mode
    fields.viewport_capability_names.Text <- String.Join(", ", config.viewport_capabilities.viewports)
    fields.right_click_flight_entry_names.Text <- String.Join(", ", config.right_click_flight_entry.viewports)
    set_checked options.normalize_diagonal_movement config.normalize_diagonal_movement
    set_checked options.hide_gumball_while_flying config.hide_gumball_while_flying
    set_checked options.save_speed_to_document config.save_speed_to_document
    set_checked options.load_speed_from_document config.load_speed_from_document
    set_checked options.wheel_changes_speed_during_flight_navigation config.wheel_changes_speed_during_flight_navigation
    set_checked options.mouse4_action_while_flying config.mouse4_action_while_flying
    set_checked options.mouse5_action_while_flying config.mouse5_action_while_flying
    set_checked options.middle_mouse_action_while_flying config.middle_mouse_action_while_flying
    set_checked options.middle_mouse_uses_cursor_outside_flight config.middle_mouse_uses_cursor_outside_flight
    set_checked options.mouse4_uses_cursor_outside_flight config.mouse4_uses_cursor_outside_flight
    set_checked options.mouse5_uses_cursor_outside_flight config.mouse5_uses_cursor_outside_flight
    set_checked options.exit_on_mouse_left config.exit_on_mouse_left
    set_checked options.exit_on_mouse_right config.exit_on_mouse_right
    set_checked options.commands_do_not_repeat config.commands_do_not_repeat

let read (fields: SettingsFields.ConfigFields) =
    let bindings = fields.bindings
    let modes = fields.modes
    let options = fields.options

    let viewport_names (field: TextBox) =
        let names =
            field.Text.Split([| ',' |], StringSplitOptions.RemoveEmptyEntries)
            |> Array.map (fun (value: string) -> value.Trim())
            |> Array.filter (String.IsNullOrWhiteSpace >> not)
            |> Array.distinctBy (fun (value: string) -> value.ToUpperInvariant())

        field.Text <- String.Join(", ", names)
        names

    let viewportCapabilityNames = viewport_names fields.viewport_capability_names

    let rightClickFlightEntryNames =
        viewport_names fields.right_click_flight_entry_names

    match parse_numbers fields.numbers with
    | Error error -> Error error
    | Ok numbers ->
        Ok
            { config_version = ConfigSchema.CURRENT_VERSION
              enabled = is_checked options.enabled
              forward = bindings.forward.Text
              backward = bindings.backward.Text
              left = bindings.left.Text
              right = bindings.right.Text
              up = bindings.up.Text
              down = bindings.down.Text
              key_pivot_left = bindings.key_pivot_left.Text
              key_pivot_right = bindings.key_pivot_right.Text
              pivot_toggle = bindings.pivot_toggle.Text
              pivot_hold = bindings.pivot_hold.Text
              pan_toggle = bindings.pan_toggle.Text
              pan_hold = bindings.pan_hold.Text
              boost = bindings.boost.Text
              slow = bindings.slow.Text
              speed_increase = bindings.speed_increase.Text
              speed_decrease = bindings.speed_decrease.Text
              retarget_all_views = bindings.retarget_all_views.Text
              retarget_other_views = bindings.retarget_other_views.Text
              untilt_view = bindings.untilt_view.Text
              exit_key = bindings.exit_key.Text
              cancel_flight_and_restore = bindings.cancel_flight_and_restore.Text
              toggle_projection = bindings.toggle_projection.Text
              base_speed = numbers.base_speed
              minimum_speed = numbers.minimum_speed
              maximum_speed = numbers.maximum_speed
              speed_step_multiplier = numbers.speed_step_multiplier
              boost_multiplier = numbers.boost_multiplier
              slow_multiplier = numbers.slow_multiplier
              key_pivot_speed_multiplier = numbers.key_pivot_speed_multiplier
              mouse_pivot_multiplier = numbers.mouse_pivot_multiplier
              mouse_pan_multiplier = numbers.mouse_pan_multiplier
              perspective_retarget_fallback_multiplier = numbers.perspective_retarget_fallback_multiplier
              parallel_retarget_fallback_multiplier = numbers.parallel_retarget_fallback_multiplier
              perspective_retarget_zoom_border = numbers.perspective_retarget_zoom_border
              parallel_retarget_zoom_border = numbers.parallel_retarget_zoom_border
              parallel_mouse_sensitivity = numbers.parallel_mouse_sensitivity
              parallel_mouse_pivot_multiplier = numbers.parallel_mouse_pivot_multiplier
              parallel_mouse_pan_multiplier = numbers.parallel_mouse_pan_multiplier
              parallel_zoom_speed_multiplier = numbers.parallel_zoom_speed_multiplier
              parallel_up_down_multiplier = numbers.parallel_up_down_multiplier
              mouse_sensitivity = numbers.mouse_sensitivity
              mouse_x_mode = SettingsFields.selected_mode modes.mouse_x_mode
              mouse_y_mode = SettingsFields.selected_mode modes.mouse_y_mode
              normalize_diagonal_movement = is_checked options.normalize_diagonal_movement
              hide_gumball_while_flying = is_checked options.hide_gumball_while_flying
              prioritized_target = SettingsFields.selected_mode modes.prioritized_target
              save_speed_to_document = is_checked options.save_speed_to_document
              load_speed_from_document = is_checked options.load_speed_from_document
              wheel_speed_mode = SettingsFields.selected_mode modes.wheel_speed_mode
              wheel_changes_speed_during_flight_navigation =
                is_checked options.wheel_changes_speed_during_flight_navigation
              exit_on_mouse_left = is_checked options.exit_on_mouse_left
              exit_on_mouse_right = is_checked options.exit_on_mouse_right
              middle_mouse_action_while_flying = is_checked options.middle_mouse_action_while_flying
              middle_mouse_uses_cursor_outside_flight = is_checked options.middle_mouse_uses_cursor_outside_flight
              mouse4_uses_cursor_outside_flight = is_checked options.mouse4_uses_cursor_outside_flight
              mouse5_uses_cursor_outside_flight = is_checked options.mouse5_uses_cursor_outside_flight
              mouse4_action_while_flying = is_checked options.mouse4_action_while_flying
              mouse5_action_while_flying = is_checked options.mouse5_action_while_flying
              right_click_entry_mode = SettingsFields.selected_mode modes.right_click_entry_mode
              default_flight_mode = SettingsFields.selected_mode modes.default_flight_mode
              shift_right_click_retarget = SettingsFields.selected_mode modes.shift_right_click_retarget
              alt_right_click_retarget = SettingsFields.selected_mode modes.alt_right_click_retarget
              ctrl_right_click_retarget = SettingsFields.selected_mode modes.ctrl_right_click_retarget
              middle_mouse_retarget = SettingsFields.selected_mode modes.middle_mouse_retarget
              mouse4_retarget = SettingsFields.selected_mode modes.mouse4_retarget
              mouse5_retarget = SettingsFields.selected_mode modes.mouse5_retarget
              retarget_all_views_mode = SettingsFields.selected_mode modes.retarget_all_views_mode
              retarget_other_views_mode = SettingsFields.selected_mode modes.retarget_other_views_mode
              retarget_on_pivot = SettingsFields.selected_mode modes.retarget_on_pivot
              retarget_on_pan = SettingsFields.selected_mode modes.retarget_on_pan
              retarget_on_flight_exit = SettingsFields.selected_mode modes.retarget_on_flight_exit
              retarget_on_restored_flight_exit = SettingsFields.selected_mode modes.retarget_on_restored_flight_exit
              viewport_capabilities =
                { mode = SettingsFields.selected_mode modes.viewport_capabilities
                  viewports = viewportCapabilityNames }
              right_click_flight_entry =
                { mode = SettingsFields.selected_mode modes.right_click_flight_entry
                  viewports = rightClickFlightEntryNames }
              commands_do_not_repeat = is_checked options.commands_do_not_repeat
              mouse4_action = SettingsFields.selected_mode modes.mouse4_action
              mouse5_action = SettingsFields.selected_mode modes.mouse5_action
              middle_mouse_action = SettingsFields.selected_mode modes.middle_mouse_action
              shift_right_click_action = SettingsFields.selected_mode modes.shift_right_click_action
              alt_right_click_action = SettingsFields.selected_mode modes.alt_right_click_action
              ctrl_right_click_action = SettingsFields.selected_mode modes.ctrl_right_click_action
              boost_mode = SettingsFields.selected_mode modes.boost_mode
              slow_mode = SettingsFields.selected_mode modes.slow_mode
              vertical_speed_multiplier = numbers.vertical_speed_multiplier
              perspective_lens_length_after_parallel_mm = numbers.perspective_lens_length_after_parallel_mm
              forced_perspective_lens_length_on_flight_start_mm =
                numbers.forced_perspective_lens_length_on_flight_start_mm
              perspective_lens_length_delta_during_flight_mm = numbers.perspective_lens_length_delta_during_flight_mm
              viewport_paint_mode = SettingsFields.selected_mode modes.viewport_paint_mode }
