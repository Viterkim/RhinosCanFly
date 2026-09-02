module RhinosCanFly.ConfigCompiler

open System

type ConfigIssue =
    { setting: string
      message: string
      repair: FlyConfigFile -> FlyConfigFile }

type PositiveCheck = string * float * (FlyConfigFile -> FlyConfigFile)
type DerivedCheck = string * string * float * (FlyConfigFile -> FlyConfigFile)
type EnumCheck<'Value> = string * 'Value * (FlyConfigFile -> FlyConfigFile)

let format_issues (issues: ConfigIssue list) =
    issues
    |> List.map (fun (issue: ConfigIssue) -> issue.message)
    |> String.concat Environment.NewLine

let compile_detailed (source: FlyConfigFile) =
    let defaults = ConfigSchema.defaults
    let issues = ResizeArray<ConfigIssue>()

    let add_issue (setting: string) (message: string) (repair: FlyConfigFile -> FlyConfigFile) =
        issues.Add
            { setting = setting
              message = message
              repair = repair }

    let reset_speed_values (config: FlyConfigFile) =
        { config with
            base_speed = defaults.base_speed
            minimum_speed = defaults.minimum_speed
            maximum_speed = defaults.maximum_speed }

    let reset_movement_limits (config: FlyConfigFile) =
        { reset_speed_values config with
            boost_multiplier = defaults.boost_multiplier
            slow_multiplier = defaults.slow_multiplier }

    let while_flying (enabled: bool) (action: MouseGestureAction) (retargetMode: RetargetMode) =
        if enabled then
            RoutedMouseAction.create action retargetMode
        else
            RoutedMouseAction.Off

    let viewport_name_list
        (setting: string)
        (sourceValue: ViewportNameListFile)
        (defaultValue: ViewportNameListFile)
        (repair: FlyConfigFile -> FlyConfigFile)
        =
        let viewports =
            if isNull (box sourceValue) || isNull sourceValue.viewports then
                Array.empty
            else
                sourceValue.viewports
                |> Array.map (fun (value: string) -> if isNull value then "" else value.Trim())
                |> Array.filter (String.IsNullOrWhiteSpace >> not)
                |> Array.distinctBy (fun (value: string) -> value.ToUpperInvariant())

        let compile_mode (mode: ViewportNameListMode) (names: string array) =
            match mode with
            | ViewportNameListMode.EnabledAll -> ViewportNameList.EnabledAll
            | ViewportNameListMode.EnabledSome -> ViewportNameList.EnabledSome names
            | ViewportNameListMode.DisabledSome -> ViewportNameList.DisabledSome names
            | ViewportNameListMode.DisabledAll -> ViewportNameList.DisabledAll
            | _ -> ViewportNameList.DisabledAll

        if isNull (box sourceValue) then
            add_issue setting $"{setting} is missing" repair
            compile_mode defaultValue.mode defaultValue.viewports
        else
            match sourceValue.mode with
            | ViewportNameListMode.EnabledAll
            | ViewportNameListMode.EnabledSome
            | ViewportNameListMode.DisabledSome
            | ViewportNameListMode.DisabledAll -> compile_mode sourceValue.mode viewports
            | _ ->
                add_issue setting $"{setting}.mode is invalid" repair
                compile_mode defaultValue.mode defaultValue.viewports

    let viewportCapabilities =
        viewport_name_list
            "viewport_capabilities"
            source.viewport_capabilities
            defaults.viewport_capabilities
            (fun (config: FlyConfigFile) ->
                { config with
                    viewport_capabilities = defaults.viewport_capabilities })

    let rightClickFlightEntry =
        viewport_name_list
            "right_click_flight_entry"
            source.right_click_flight_entry
            defaults.right_click_flight_entry
            (fun (config: FlyConfigFile) ->
                { config with
                    right_click_flight_entry = defaults.right_click_flight_entry })

    let required (name: string) (value: string) (repair: FlyConfigFile -> FlyConfigFile) =
        match PlatformBindings.parse value with
        | Ok key -> key
        | Error error ->
            add_issue name $"{name}: {error}" repair

            { virtual_keys = Array.empty<VirtualKey> }

    let optional (name: string) (value: string) (repair: FlyConfigFile -> FlyConfigFile) =
        if String.IsNullOrWhiteSpace value then
            None
        else
            Some(required name value repair)

    let positive (name: string) (value: float) (repair: FlyConfigFile -> FlyConfigFile) =
        if Double.IsNaN value || Double.IsInfinity value || value <= 0. then
            add_issue name $"{name} must be a positive finite number" repair

    let enum_value (name: string) (enumType: Type) (value: obj) (repair: FlyConfigFile -> FlyConfigFile) =
        if not (Enum.IsDefined(enumType, value)) then
            add_issue name $"{name} is invalid" repair

    let positiveChecks: PositiveCheck list =
        [ "base_speed",
          source.base_speed,
          (fun (config: FlyConfigFile) ->
              { config with
                  base_speed = defaults.base_speed })
          "minimum_speed",
          source.minimum_speed,
          (fun (config: FlyConfigFile) ->
              { config with
                  minimum_speed = defaults.minimum_speed })
          "maximum_speed",
          source.maximum_speed,
          (fun (config: FlyConfigFile) ->
              { config with
                  maximum_speed = defaults.maximum_speed })
          "speed_step_multiplier",
          source.speed_step_multiplier,
          (fun (config: FlyConfigFile) ->
              { config with
                  speed_step_multiplier = defaults.speed_step_multiplier })
          "boost_multiplier",
          source.boost_multiplier,
          (fun (config: FlyConfigFile) ->
              { config with
                  boost_multiplier = defaults.boost_multiplier })
          "slow_multiplier",
          source.slow_multiplier,
          (fun (config: FlyConfigFile) ->
              { config with
                  slow_multiplier = defaults.slow_multiplier })
          "key_pivot_speed_multiplier",
          source.key_pivot_speed_multiplier,
          (fun (config: FlyConfigFile) ->
              { config with
                  key_pivot_speed_multiplier = defaults.key_pivot_speed_multiplier })
          "mouse_pivot_multiplier",
          source.mouse_pivot_multiplier,
          (fun (config: FlyConfigFile) ->
              { config with
                  mouse_pivot_multiplier = defaults.mouse_pivot_multiplier })
          "mouse_pan_multiplier",
          source.mouse_pan_multiplier,
          (fun (config: FlyConfigFile) ->
              { config with
                  mouse_pan_multiplier = defaults.mouse_pan_multiplier })
          "mouse_sensitivity",
          source.mouse_sensitivity,
          (fun (config: FlyConfigFile) ->
              { config with
                  mouse_sensitivity = defaults.mouse_sensitivity })
          "perspective_retarget_fallback_multiplier",
          source.perspective_retarget_fallback_multiplier,
          (fun (config: FlyConfigFile) ->
              { config with
                  perspective_retarget_fallback_multiplier = defaults.perspective_retarget_fallback_multiplier })
          "parallel_retarget_fallback_multiplier",
          source.parallel_retarget_fallback_multiplier,
          (fun (config: FlyConfigFile) ->
              { config with
                  parallel_retarget_fallback_multiplier = defaults.parallel_retarget_fallback_multiplier })
          "perspective_retarget_zoom_border",
          source.perspective_retarget_zoom_border,
          (fun (config: FlyConfigFile) ->
              { config with
                  perspective_retarget_zoom_border = defaults.perspective_retarget_zoom_border })
          "parallel_retarget_zoom_border",
          source.parallel_retarget_zoom_border,
          (fun (config: FlyConfigFile) ->
              { config with
                  parallel_retarget_zoom_border = defaults.parallel_retarget_zoom_border })
          "vertical_speed_multiplier",
          source.vertical_speed_multiplier,
          (fun (config: FlyConfigFile) ->
              { config with
                  vertical_speed_multiplier = defaults.vertical_speed_multiplier })
          "parallel_mouse_sensitivity",
          source.parallel_mouse_sensitivity,
          (fun (config: FlyConfigFile) ->
              { config with
                  parallel_mouse_sensitivity = defaults.parallel_mouse_sensitivity })
          "parallel_mouse_pivot_multiplier",
          source.parallel_mouse_pivot_multiplier,
          (fun (config: FlyConfigFile) ->
              { config with
                  parallel_mouse_pivot_multiplier = defaults.parallel_mouse_pivot_multiplier })
          "parallel_mouse_pan_multiplier",
          source.parallel_mouse_pan_multiplier,
          (fun (config: FlyConfigFile) ->
              { config with
                  parallel_mouse_pan_multiplier = defaults.parallel_mouse_pan_multiplier })
          "parallel_zoom_speed_multiplier",
          source.parallel_zoom_speed_multiplier,
          (fun (config: FlyConfigFile) ->
              { config with
                  parallel_zoom_speed_multiplier = defaults.parallel_zoom_speed_multiplier })
          "parallel_up_down_multiplier",
          source.parallel_up_down_multiplier,
          (fun (config: FlyConfigFile) ->
              { config with
                  parallel_up_down_multiplier = defaults.parallel_up_down_multiplier })
          "perspective_lens_length_after_parallel_mm",
          source.perspective_lens_length_after_parallel_mm,
          (fun (config: FlyConfigFile) ->
              { config with
                  perspective_lens_length_after_parallel_mm = defaults.perspective_lens_length_after_parallel_mm }) ]

    positiveChecks
    |> List.iter (fun (check: PositiveCheck) ->
        let name, value, repair = check
        positive name value repair)

    if source.maximum_speed < source.minimum_speed then
        add_issue
            "movement_speed_range"
            "maximum_speed must be greater than or equal to minimum_speed"
            reset_speed_values

    if
        source.base_speed < source.minimum_speed
        || source.base_speed > source.maximum_speed
    then
        add_issue "movement_speed_range" "base_speed must be between minimum_speed and maximum_speed" reset_speed_values

    if source.speed_step_multiplier <= 1. then
        add_issue "speed_step_multiplier" "speed_step_multiplier must be greater than 1" (fun (config: FlyConfigFile) ->
            { config with
                speed_step_multiplier = defaults.speed_step_multiplier })

    if
        Double.IsNaN source.forced_perspective_lens_length_on_flight_start_mm
        || Double.IsInfinity source.forced_perspective_lens_length_on_flight_start_mm
        || source.forced_perspective_lens_length_on_flight_start_mm < 0.
    then
        add_issue
            "forced_perspective_lens_length_on_flight_start_mm"
            "forced_perspective_lens_length_on_flight_start_mm must be 0 (disabled) or a positive finite number"
            (fun (config: FlyConfigFile) ->
                { config with
                    forced_perspective_lens_length_on_flight_start_mm =
                        defaults.forced_perspective_lens_length_on_flight_start_mm })

    if
        Double.IsNaN source.perspective_lens_length_delta_during_flight_mm
        || Double.IsInfinity source.perspective_lens_length_delta_during_flight_mm
    then
        add_issue
            "perspective_lens_length_delta_during_flight_mm"
            "perspective_lens_length_delta_during_flight_mm must be a finite number"
            (fun (config: FlyConfigFile) ->
                { config with
                    perspective_lens_length_delta_during_flight_mm =
                        defaults.perspective_lens_length_delta_during_flight_mm })

    if source.forced_perspective_lens_length_on_flight_start_mm > 0. then
        let adjustedLens =
            source.forced_perspective_lens_length_on_flight_start_mm
            + source.perspective_lens_length_delta_during_flight_mm

        if
            Double.IsNaN adjustedLens
            || Double.IsInfinity adjustedLens
            || adjustedLens <= 0.
        then
            add_issue
                "perspective_lens_while_flying"
                "forced_perspective_lens_length_on_flight_start_mm plus perspective_lens_length_delta_during_flight_mm must be a positive finite number"
                (fun (config: FlyConfigFile) ->
                    { config with
                        perspective_lens_length_delta_during_flight_mm =
                            defaults.perspective_lens_length_delta_during_flight_mm })

    let combinedMovementMultiplier = source.boost_multiplier * source.slow_multiplier

    let maximumMovementMultiplier =
        max 1. (max source.boost_multiplier (max source.slow_multiplier combinedMovementMultiplier))

    let derivedChecks: DerivedCheck list =
        [ "movement_speed_multipliers",
          "combined boost and slow multiplier",
          combinedMovementMultiplier,
          (fun (config: FlyConfigFile) ->
              { config with
                  boost_multiplier = defaults.boost_multiplier
                  slow_multiplier = defaults.slow_multiplier })
          "movement_limits",
          "maximum movement speed",
          source.maximum_speed * maximumMovementMultiplier,
          reset_movement_limits
          "vertical_speed_multiplier",
          "maximum vertical movement speed",
          source.maximum_speed
          * maximumMovementMultiplier
          * source.vertical_speed_multiplier,
          (fun (config: FlyConfigFile) ->
              { config with
                  vertical_speed_multiplier = defaults.vertical_speed_multiplier })
          "parallel_up_down_multiplier",
          "maximum parallel up/down speed",
          source.maximum_speed
          * maximumMovementMultiplier
          * source.parallel_up_down_multiplier,
          (fun (config: FlyConfigFile) ->
              { config with
                  parallel_up_down_multiplier = defaults.parallel_up_down_multiplier })
          "parallel_zoom_speed_multiplier",
          "maximum parallel zoom speed",
          source.maximum_speed
          * maximumMovementMultiplier
          * source.parallel_zoom_speed_multiplier,
          (fun (config: FlyConfigFile) ->
              { config with
                  parallel_zoom_speed_multiplier = defaults.parallel_zoom_speed_multiplier })
          "parallel_mouse_sensitivity",
          "maximum parallel mouse sensitivity",
          source.parallel_mouse_sensitivity
          * max 1. (max source.parallel_mouse_pivot_multiplier source.parallel_mouse_pan_multiplier),
          (fun (config: FlyConfigFile) ->
              { config with
                  parallel_mouse_sensitivity = defaults.parallel_mouse_sensitivity
                  parallel_mouse_pivot_multiplier = defaults.parallel_mouse_pivot_multiplier
                  parallel_mouse_pan_multiplier = defaults.parallel_mouse_pan_multiplier })
          "key_pivot_speed_multiplier",
          "key pivot angular speed",
          source.key_pivot_speed_multiplier * Math.PI / 6.,
          (fun (config: FlyConfigFile) ->
              { config with
                  key_pivot_speed_multiplier = defaults.key_pivot_speed_multiplier })
          "mouse_pivot_sensitivity",
          "mouse pivot sensitivity",
          source.mouse_sensitivity * source.mouse_pivot_multiplier,
          (fun (config: FlyConfigFile) ->
              { config with
                  mouse_sensitivity = defaults.mouse_sensitivity
                  mouse_pivot_multiplier = defaults.mouse_pivot_multiplier })
          "mouse_pan_sensitivity",
          "mouse pan sensitivity",
          source.mouse_sensitivity * source.mouse_pan_multiplier,
          (fun (config: FlyConfigFile) ->
              { config with
                  mouse_sensitivity = defaults.mouse_sensitivity
                  mouse_pan_multiplier = defaults.mouse_pan_multiplier })
          "perspective_retarget_fallback_multiplier",
          "maximum perspective retarget fallback distance",
          source.maximum_speed * source.perspective_retarget_fallback_multiplier,
          (fun (config: FlyConfigFile) ->
              { config with
                  perspective_retarget_fallback_multiplier = defaults.perspective_retarget_fallback_multiplier })
          "parallel_retarget_fallback_multiplier",
          "maximum parallel retarget fallback distance",
          source.maximum_speed * source.parallel_retarget_fallback_multiplier,
          (fun (config: FlyConfigFile) ->
              { config with
                  parallel_retarget_fallback_multiplier = defaults.parallel_retarget_fallback_multiplier }) ]

    derivedChecks
    |> List.iter (fun (check: DerivedCheck) ->
        let setting, description, value, repair = check

        if Double.IsNaN value || Double.IsInfinity value then
            add_issue setting $"{description} is too large" repair)

    enum_value "mouse_x_mode" typeof<MouseAxisMode> (box source.mouse_x_mode) (fun (config: FlyConfigFile) ->
        { config with
            mouse_x_mode = defaults.mouse_x_mode })

    enum_value "mouse_y_mode" typeof<MouseAxisMode> (box source.mouse_y_mode) (fun (config: FlyConfigFile) ->
        { config with
            mouse_y_mode = defaults.mouse_y_mode })

    enum_value
        "wheel_speed_mode"
        typeof<MouseWheelSpeedMode>
        (box source.wheel_speed_mode)
        (fun (config: FlyConfigFile) ->
            { config with
                wheel_speed_mode = defaults.wheel_speed_mode })

    enum_value
        "right_click_entry_mode"
        typeof<RightClickEntryMode>
        (box source.right_click_entry_mode)
        (fun (config: FlyConfigFile) ->
            { config with
                right_click_entry_mode = defaults.right_click_entry_mode })

    enum_value
        "default_flight_mode"
        typeof<DefaultFlightMode>
        (box source.default_flight_mode)
        (fun (config: FlyConfigFile) ->
            { config with
                default_flight_mode = defaults.default_flight_mode })

    enum_value
        "prioritized_target"
        typeof<PrioritizedTarget>
        (box source.prioritized_target)
        (fun (config: FlyConfigFile) ->
            { config with
                prioritized_target = defaults.prioritized_target })

    let retargetChecks: EnumCheck<RetargetMode> list =
        [ "shift_right_click_retarget",
          source.shift_right_click_retarget,
          (fun (config: FlyConfigFile) ->
              { config with
                  shift_right_click_retarget = defaults.shift_right_click_retarget })
          "alt_right_click_retarget",
          source.alt_right_click_retarget,
          (fun (config: FlyConfigFile) ->
              { config with
                  alt_right_click_retarget = defaults.alt_right_click_retarget })
          "ctrl_right_click_retarget",
          source.ctrl_right_click_retarget,
          (fun (config: FlyConfigFile) ->
              { config with
                  ctrl_right_click_retarget = defaults.ctrl_right_click_retarget })
          "middle_mouse_retarget",
          source.middle_mouse_retarget,
          (fun (config: FlyConfigFile) ->
              { config with
                  middle_mouse_retarget = defaults.middle_mouse_retarget })
          "mouse4_retarget",
          source.mouse4_retarget,
          (fun (config: FlyConfigFile) ->
              { config with
                  mouse4_retarget = defaults.mouse4_retarget })
          "mouse5_retarget",
          source.mouse5_retarget,
          (fun (config: FlyConfigFile) ->
              { config with
                  mouse5_retarget = defaults.mouse5_retarget })
          "retarget_all_views_mode",
          source.retarget_all_views_mode,
          (fun (config: FlyConfigFile) ->
              { config with
                  retarget_all_views_mode = defaults.retarget_all_views_mode })
          "retarget_other_views_mode",
          source.retarget_other_views_mode,
          (fun (config: FlyConfigFile) ->
              { config with
                  retarget_other_views_mode = defaults.retarget_other_views_mode })
          "retarget_on_pivot",
          source.retarget_on_pivot,
          (fun (config: FlyConfigFile) ->
              { config with
                  retarget_on_pivot = defaults.retarget_on_pivot })
          "retarget_on_pan",
          source.retarget_on_pan,
          (fun (config: FlyConfigFile) ->
              { config with
                  retarget_on_pan = defaults.retarget_on_pan })
          "retarget_on_flight_exit",
          source.retarget_on_flight_exit,
          (fun (config: FlyConfigFile) ->
              { config with
                  retarget_on_flight_exit = defaults.retarget_on_flight_exit })
          "retarget_on_restored_flight_exit",
          source.retarget_on_restored_flight_exit,
          (fun (config: FlyConfigFile) ->
              { config with
                  retarget_on_restored_flight_exit = defaults.retarget_on_restored_flight_exit }) ]

    retargetChecks
    |> List.iter (fun (check: EnumCheck<RetargetMode>) ->
        let name, value, repair = check
        enum_value name typeof<RetargetMode> (box value) repair)

    let actionChecks: EnumCheck<MouseGestureAction> list =
        [ "mouse4_action",
          source.mouse4_action,
          (fun (config: FlyConfigFile) ->
              { config with
                  mouse4_action = defaults.mouse4_action })
          "mouse5_action",
          source.mouse5_action,
          (fun (config: FlyConfigFile) ->
              { config with
                  mouse5_action = defaults.mouse5_action })
          "middle_mouse_action",
          source.middle_mouse_action,
          (fun (config: FlyConfigFile) ->
              { config with
                  middle_mouse_action = defaults.middle_mouse_action })
          "shift_right_click_action",
          source.shift_right_click_action,
          (fun (config: FlyConfigFile) ->
              { config with
                  shift_right_click_action = defaults.shift_right_click_action })
          "alt_right_click_action",
          source.alt_right_click_action,
          (fun (config: FlyConfigFile) ->
              { config with
                  alt_right_click_action = defaults.alt_right_click_action })
          "ctrl_right_click_action",
          source.ctrl_right_click_action,
          (fun (config: FlyConfigFile) ->
              { config with
                  ctrl_right_click_action = defaults.ctrl_right_click_action }) ]

    actionChecks
    |> List.iter (fun (check: EnumCheck<MouseGestureAction>) ->
        let name, value, repair = check
        enum_value name typeof<MouseGestureAction> (box value) repair)

    enum_value "boost_mode" typeof<KeyActivationMode> (box source.boost_mode) (fun (config: FlyConfigFile) ->
        { config with
            boost_mode = defaults.boost_mode })

    enum_value "slow_mode" typeof<KeyActivationMode> (box source.slow_mode) (fun (config: FlyConfigFile) ->
        { config with
            slow_mode = defaults.slow_mode })

    enum_value
        "viewport_paint_mode"
        typeof<ViewportPaintMode>
        (box source.viewport_paint_mode)
        (fun (config: FlyConfigFile) ->
            { config with
                viewport_paint_mode = defaults.viewport_paint_mode })

    let config: FlyConfig =
        { bindings =
            { forward =
                required "forward" source.forward (fun (config: FlyConfigFile) ->
                    { config with
                        forward = defaults.forward })
              backward =
                required "backward" source.backward (fun (config: FlyConfigFile) ->
                    { config with
                        backward = defaults.backward })
              left = required "left" source.left (fun (config: FlyConfigFile) -> { config with left = defaults.left })
              right =
                required "right" source.right (fun (config: FlyConfigFile) -> { config with right = defaults.right })
              up = required "up" source.up (fun (config: FlyConfigFile) -> { config with up = defaults.up })
              down = required "down" source.down (fun (config: FlyConfigFile) -> { config with down = defaults.down })
              key_pivot_left =
                required "key_pivot_left" source.key_pivot_left (fun (config: FlyConfigFile) ->
                    { config with
                        key_pivot_left = defaults.key_pivot_left })
              key_pivot_right =
                required "key_pivot_right" source.key_pivot_right (fun (config: FlyConfigFile) ->
                    { config with
                        key_pivot_right = defaults.key_pivot_right })
              mouse_navigation =
                { pivot =
                    { toggle =
                        optional "pivot_toggle" source.pivot_toggle (fun (config: FlyConfigFile) ->
                            { config with
                                pivot_toggle = defaults.pivot_toggle })
                      hold =
                        optional "pivot_hold" source.pivot_hold (fun (config: FlyConfigFile) ->
                            { config with
                                pivot_hold = defaults.pivot_hold }) }
                  pan =
                    { toggle =
                        optional "pan_toggle" source.pan_toggle (fun (config: FlyConfigFile) ->
                            { config with
                                pan_toggle = defaults.pan_toggle })
                      hold =
                        optional "pan_hold" source.pan_hold (fun (config: FlyConfigFile) ->
                            { config with
                                pan_hold = defaults.pan_hold }) } }
              boost =
                required "boost" source.boost (fun (config: FlyConfigFile) -> { config with boost = defaults.boost })
              slow = required "slow" source.slow (fun (config: FlyConfigFile) -> { config with slow = defaults.slow })
              speed_increase =
                optional "speed_increase" source.speed_increase (fun (config: FlyConfigFile) ->
                    { config with
                        speed_increase = defaults.speed_increase })
              speed_decrease =
                optional "speed_decrease" source.speed_decrease (fun (config: FlyConfigFile) ->
                    { config with
                        speed_decrease = defaults.speed_decrease })
              retarget_all_views =
                optional "retarget_all_views" source.retarget_all_views (fun (config: FlyConfigFile) ->
                    { config with
                        retarget_all_views = defaults.retarget_all_views })
              retarget_other_views =
                optional "retarget_other_views" source.retarget_other_views (fun (config: FlyConfigFile) ->
                    { config with
                        retarget_other_views = defaults.retarget_other_views })
              untilt_view =
                optional "untilt_view" source.untilt_view (fun (config: FlyConfigFile) ->
                    { config with
                        untilt_view = defaults.untilt_view })
              exit_key =
                required "exit_key" source.exit_key (fun (config: FlyConfigFile) ->
                    { config with
                        exit_key = defaults.exit_key })
              cancel_flight_and_restore =
                required "cancel_flight_and_restore" source.cancel_flight_and_restore (fun (config: FlyConfigFile) ->
                    { config with
                        cancel_flight_and_restore = defaults.cancel_flight_and_restore })
              toggle_projection =
                optional "toggle_projection" source.toggle_projection (fun (config: FlyConfigFile) ->
                    { config with
                        toggle_projection = defaults.toggle_projection }) }
          viewport_access =
            { capabilities = viewportCapabilities
              right_click_flight_entry = rightClickFlightEntry }
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
              parallel_projection =
                { mouse_sensitivity =
                    source.parallel_mouse_sensitivity
                    |> MouseSensitivitySetting
                    |> MouseSensitivity.to_radians_per_count
                  mouse_pivot_multiplier = MousePivotMultiplier source.parallel_mouse_pivot_multiplier
                  mouse_pan_multiplier = MousePanMultiplier source.parallel_mouse_pan_multiplier
                  zoom_speed_multiplier = source.parallel_zoom_speed_multiplier
                  up_down_multiplier = source.parallel_up_down_multiplier } }
          mouse =
            { pivot_multiplier = MousePivotMultiplier source.mouse_pivot_multiplier
              pan_multiplier = MousePanMultiplier source.mouse_pan_multiplier
              sensitivity =
                source.mouse_sensitivity
                |> MouseSensitivitySetting
                |> MouseSensitivity.to_radians_per_count
              x_mode = source.mouse_x_mode
              y_mode = source.mouse_y_mode
              exit_on_left = source.exit_on_mouse_left
              exit_on_right = source.exit_on_mouse_right
              middle_button =
                while_flying
                    source.middle_mouse_action_while_flying
                    source.middle_mouse_action
                    source.middle_mouse_retarget
              mouse4 = while_flying source.mouse4_action_while_flying source.mouse4_action source.mouse4_retarget
              mouse5 = while_flying source.mouse5_action_while_flying source.mouse5_action source.mouse5_retarget }
          behavior =
            { hide_gumball = source.hide_gumball_while_flying
              prioritized_target = source.prioritized_target
              retarget =
                { keyboard_all_views = source.retarget_all_views_mode
                  keyboard_other_views = source.retarget_other_views_mode
                  shift_right_click = source.shift_right_click_retarget
                  alt_right_click = source.alt_right_click_retarget
                  ctrl_right_click = source.ctrl_right_click_retarget
                  middle_mouse = source.middle_mouse_retarget
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
                { after_parallel = PerspectiveLensLengthMm source.perspective_lens_length_after_parallel_mm
                  forced_on_flight_start =
                    if source.forced_perspective_lens_length_on_flight_start_mm = 0. then
                        None
                    else
                        Some(PerspectiveLensLengthMm source.forced_perspective_lens_length_on_flight_start_mm)
                  delta_during_flight = PerspectiveLensDeltaMm source.perspective_lens_length_delta_during_flight_mm }
              viewport_paint_mode = source.viewport_paint_mode } }

    if issues.Count = 0 then
        Ok config
    else
        Error(List.ofSeq issues)

let compile (source: FlyConfigFile) =
    match compile_detailed source with
    | Ok config -> Ok config
    | Error issues -> Error(format_issues issues)
