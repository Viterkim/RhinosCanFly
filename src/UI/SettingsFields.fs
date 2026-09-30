module RhinosCanFly.SettingsFields

open Eto.Forms

type ModeField<'Mode> =
    { control: DropDown
      options: ('Mode * string) array
      fallback: 'Mode }

type BindingFields =
    { forward: TextBox
      backward: TextBox
      left: TextBox
      right: TextBox
      up: TextBox
      down: TextBox
      key_pivot_left: TextBox
      key_pivot_right: TextBox
      pivot_toggle: TextBox
      pivot_hold: TextBox
      pan_toggle: TextBox
      pan_hold: TextBox
      boost: TextBox
      slow: TextBox
      speed_increase: TextBox
      speed_decrease: TextBox
      retarget_all_views: TextBox
      retarget_other_views: TextBox
      untilt_view: TextBox
      exit_key: TextBox
      cancel_flight_and_restore: TextBox
      toggle_projection: TextBox }

type NumberFields =
    { base_speed: TextBox
      minimum_speed: TextBox
      maximum_speed: TextBox
      speed_step_multiplier: TextBox
      boost_multiplier: TextBox
      slow_multiplier: TextBox
      vertical_speed_multiplier: TextBox
      key_pivot_speed_multiplier: TextBox
      mouse_pivot_multiplier: TextBox
      mouse_pan_multiplier: TextBox
      retarget_base_distance: TextBox
      perspective_retarget_fallback_multiplier: TextBox
      parallel_retarget_fallback_multiplier: TextBox
      perspective_retarget_zoom_border: TextBox
      parallel_retarget_zoom_border: TextBox
      parallel_mouse_sensitivity: TextBox
      parallel_mouse_pivot_multiplier: TextBox
      parallel_mouse_pan_multiplier: TextBox
      parallel_zoom_speed_multiplier: TextBox
      parallel_up_down_multiplier: TextBox
      mouse_sensitivity: TextBox
      perspective_lens_length_after_parallel_mm: TextBox
      forced_perspective_lens_length_on_flight_start_mm: TextBox
      perspective_lens_length_delta_during_flight_mm: TextBox }

type ModeFields =
    { boost_mode: ModeField<KeyActivationMode>
      slow_mode: ModeField<KeyActivationMode>
      wheel_speed_mode: ModeField<MouseWheelSpeedMode>
      mouse_x_mode: ModeField<MouseAxisMode>
      mouse_y_mode: ModeField<MouseAxisMode>
      viewport_capabilities: ModeField<ViewportNameListMode>
      right_click_flight_entry: ModeField<ViewportNameListMode>
      right_click_entry_mode: ModeField<RightClickEntryMode>
      default_flight_mode: ModeField<DefaultFlightMode>
      prioritized_target: ModeField<PrioritizedTarget>
      shift_right_click_retarget: ModeField<RetargetMode>
      alt_right_click_retarget: ModeField<RetargetMode>
      ctrl_right_click_retarget: ModeField<RetargetMode>
      middle_mouse_retarget: ModeField<RetargetMode>
      mouse4_retarget: ModeField<RetargetMode>
      mouse5_retarget: ModeField<RetargetMode>
      retarget_all_views_mode: ModeField<RetargetMode>
      retarget_other_views_mode: ModeField<RetargetMode>
      retarget_on_pivot: ModeField<RetargetMode>
      retarget_on_pan: ModeField<RetargetMode>
      retarget_on_flight_exit: ModeField<RetargetMode>
      retarget_on_restored_flight_exit: ModeField<RetargetMode>
      shift_right_click_action: ModeField<MouseGestureAction>
      alt_right_click_action: ModeField<MouseGestureAction>
      ctrl_right_click_action: ModeField<MouseGestureAction>
      middle_mouse_action: ModeField<MouseGestureAction>
      mouse4_action: ModeField<MouseGestureAction>
      mouse5_action: ModeField<MouseGestureAction>
      viewport_paint_mode: ModeField<ViewportPaintMode> }

type OptionFields =
    { enabled: CheckBox
      normalize_diagonal_movement: CheckBox
      hide_gumball_while_flying: CheckBox
      save_speed_to_document: CheckBox
      load_speed_from_document: CheckBox
      wheel_changes_speed_during_flight_navigation: CheckBox
      mouse4_action_while_flying: CheckBox
      mouse5_action_while_flying: CheckBox
      middle_mouse_action_while_flying: CheckBox
      middle_mouse_uses_cursor_outside_flight: CheckBox
      mouse4_uses_cursor_outside_flight: CheckBox
      mouse5_uses_cursor_outside_flight: CheckBox
      exit_on_mouse_left: CheckBox
      exit_on_mouse_right: CheckBox
      exit_on_mouse_middle: CheckBox
      exit_on_mouse4: CheckBox
      exit_on_mouse5: CheckBox
      commands_do_not_repeat: CheckBox }

type CrosshairFields =
    { show: CheckBox
      arm_length: NumericStepper
      gap: NumericStepper
      red: NumericStepper
      green: NumericStepper
      blue: NumericStepper }

type ConfigFields =
    { crosshair: CrosshairFields
      bindings: BindingFields
      numbers: NumberFields
      modes: ModeFields
      options: OptionFields
      viewport_capability_names: TextBox
      right_click_flight_entry_names: TextBox }

type StatusFields =
    { runtime_enabled: CheckBox
      status_line: Label
      runtime_line: Label }

type RawJsonFields = { path: TextBox; contents: TextArea }

type ActionFields =
    { reset_all: Button
      raw_json_toggle: Button
      github: Button }

type Fields =
    { config: ConfigFields
      status: StatusFields
      raw_json: RawJsonFields
      actions: ActionFields }

let crosshair_number (minimum: int) (maximum: int) =
    new NumericStepper(
        MinValue = float minimum,
        MaxValue = float maximum,
        DecimalPlaces = 0,
        Increment = 1.,
        Width = 64
    )

let mode_field (options: ('Mode * string) array) (fallback: 'Mode) =
    let control = new DropDown(Height = 24)

    for _, label in options do
        control.Items.Add label

    { control = control
      options = options
      fallback = fallback }

let selected_mode (field: ModeField<'Mode>) =
    let index = field.control.SelectedIndex

    if index >= 0 && index < field.options.Length then
        fst field.options[index]
    else
        field.fallback

let set_mode (field: ModeField<'Mode>) (value: 'Mode) =
    let selected_index =
        match
            field.options
            |> Array.tryFindIndex (fun (candidate: 'Mode, _: string) -> candidate = value)
        with
        | Some index -> index
        | None ->
            field.options
            |> Array.tryFindIndex (fun (candidate: 'Mode, _: string) -> candidate = field.fallback)
            |> Option.defaultValue 0

    field.control.SelectedIndex <- selected_index

let create () =
    let activation_modes =
        [| KeyActivationMode.Toggle, "Toggle"; KeyActivationMode.Hold, "Hold" |]

    let mouse_axis_modes =
        [| MouseAxisMode.Normal, "Normal"; MouseAxisMode.Inverted, "Inverted" |]

    let wheel_speed_modes =
        [| MouseWheelSpeedMode.Off, "Off"
           MouseWheelSpeedMode.Normal, "On"
           MouseWheelSpeedMode.Reversed, "On but reversed" |]

    let mouse_gesture_actions =
        [| MouseGestureAction.Off, "Off"
           MouseGestureAction.TogglePivot, "Toggle pivot"
           MouseGestureAction.HoldPivot, "Hold pivot"
           MouseGestureAction.TogglePan, "Toggle pan"
           MouseGestureAction.HoldPan, "Hold pan"
           MouseGestureAction.Retarget, "Retarget"
           MouseGestureAction.StartFlying, "Start flying"
           MouseGestureAction.StartTempFlying, "Start temp flying" |]

    let right_click_entry_modes =
        [| RightClickEntryMode.Off, "Off"
           RightClickEntryMode.ClickToFly, "Click to fly"
           RightClickEntryMode.ClickToFlyDuringCommands, "Click to fly + during commands"
           RightClickEntryMode.HoldToFly, "Hold to fly"
           RightClickEntryMode.HoldToFlyDuringCommands, "Hold to fly + during commands" |]

    let viewport_name_list_modes =
        [| ViewportNameListMode.DisabledAll, "Off"
           ViewportNameListMode.EnabledAll, "Allow all"
           ViewportNameListMode.EnabledSome, "Allow listed"
           ViewportNameListMode.DisabledSome, "Ban listed" |]

    let flight_modes =
        [| DefaultFlightMode.Normal, "Normal"
           DefaultFlightMode.Temporary, "Temporary"
           DefaultFlightMode.TemporaryIncludingNavigationCommands, "Temporary, including Pivot/Pan commands" |]

    let retarget_modes =
        [| RetargetMode.Off, "Off"
           RetargetMode.Distance, "Distance"
           RetargetMode.SelectionCenterThenDistance, "Selection center, then distance"
           RetargetMode.SelectionCenter, "Selection center, no fallback"
           RetargetMode.GeometryThenDistance, "Geometry, then distance"
           RetargetMode.Geometry, "Geometry, no fallback"
           RetargetMode.TargetThenDistance, "Target, then distance"
           RetargetMode.Target, "Target, no fallback"
           RetargetMode.ObjectCenterThenDistance, "Object center, then distance"
           RetargetMode.ObjectCenter, "Object center, no fallback" |]

    let prioritized_targets =
        [| PrioritizedTarget.Off, "Off"
           PrioritizedTarget.SelectionCenter, "Selection center"
           PrioritizedTarget.Gumball, "Gumball" |]

    let paint_modes =
        [| ViewportPaintMode.Queued, "Normal Rhino redraw (default)"
           ViewportPaintMode.Immediate, "Immediate paint" |]

    { config =
        { crosshair =
            { show = new CheckBox(Text = "Show crosshair while flying")
              arm_length = crosshair_number 1 99999
              gap = crosshair_number 1 99999
              red = crosshair_number 0 255
              green = crosshair_number 0 255
              blue = crosshair_number 0 255 }
          bindings =
            { forward = new TextBox()
              backward = new TextBox()
              left = new TextBox()
              right = new TextBox()
              up = new TextBox()
              down = new TextBox()
              key_pivot_left = new TextBox()
              key_pivot_right = new TextBox()
              pivot_toggle = new TextBox()
              pivot_hold = new TextBox()
              pan_toggle = new TextBox()
              pan_hold = new TextBox()
              boost = new TextBox()
              slow = new TextBox()
              speed_increase = new TextBox()
              speed_decrease = new TextBox()
              retarget_all_views = new TextBox()
              retarget_other_views = new TextBox()
              untilt_view = new TextBox()
              exit_key = new TextBox()
              cancel_flight_and_restore = new TextBox()
              toggle_projection = new TextBox() }
          numbers =
            { base_speed = new TextBox()
              minimum_speed = new TextBox()
              maximum_speed = new TextBox()
              speed_step_multiplier = new TextBox()
              boost_multiplier = new TextBox()
              slow_multiplier = new TextBox()
              vertical_speed_multiplier = new TextBox()
              key_pivot_speed_multiplier = new TextBox()
              mouse_pivot_multiplier = new TextBox()
              mouse_pan_multiplier = new TextBox()
              retarget_base_distance = new TextBox()
              perspective_retarget_fallback_multiplier = new TextBox()
              parallel_retarget_fallback_multiplier = new TextBox()
              perspective_retarget_zoom_border = new TextBox()
              parallel_retarget_zoom_border = new TextBox()
              parallel_mouse_sensitivity = new TextBox()
              parallel_mouse_pivot_multiplier = new TextBox()
              parallel_mouse_pan_multiplier = new TextBox()
              parallel_zoom_speed_multiplier = new TextBox()
              parallel_up_down_multiplier = new TextBox()
              mouse_sensitivity = new TextBox()
              perspective_lens_length_after_parallel_mm = new TextBox()
              forced_perspective_lens_length_on_flight_start_mm = new TextBox()
              perspective_lens_length_delta_during_flight_mm = new TextBox() }
          modes =
            { boost_mode = mode_field activation_modes KeyActivationMode.Toggle
              slow_mode = mode_field activation_modes KeyActivationMode.Toggle
              wheel_speed_mode = mode_field wheel_speed_modes MouseWheelSpeedMode.Normal
              mouse_x_mode = mode_field mouse_axis_modes MouseAxisMode.Normal
              mouse_y_mode = mode_field mouse_axis_modes MouseAxisMode.Normal
              viewport_capabilities = mode_field viewport_name_list_modes ViewportNameListMode.DisabledAll
              right_click_flight_entry = mode_field viewport_name_list_modes ViewportNameListMode.DisabledAll
              right_click_entry_mode = mode_field right_click_entry_modes RightClickEntryMode.ClickToFlyDuringCommands
              default_flight_mode = mode_field flight_modes DefaultFlightMode.Normal
              prioritized_target = mode_field prioritized_targets PrioritizedTarget.SelectionCenter
              shift_right_click_retarget = mode_field retarget_modes RetargetMode.ObjectCenter
              alt_right_click_retarget = mode_field retarget_modes RetargetMode.ObjectCenter
              ctrl_right_click_retarget = mode_field retarget_modes RetargetMode.ObjectCenter
              middle_mouse_retarget = mode_field retarget_modes RetargetMode.ObjectCenter
              mouse4_retarget = mode_field retarget_modes RetargetMode.ObjectCenter
              mouse5_retarget = mode_field retarget_modes RetargetMode.ObjectCenter
              retarget_all_views_mode = mode_field retarget_modes RetargetMode.ObjectCenter
              retarget_other_views_mode = mode_field retarget_modes RetargetMode.ObjectCenter
              retarget_on_pivot = mode_field retarget_modes RetargetMode.ObjectCenter
              retarget_on_pan = mode_field retarget_modes RetargetMode.ObjectCenter
              retarget_on_flight_exit = mode_field retarget_modes RetargetMode.ObjectCenter
              retarget_on_restored_flight_exit = mode_field retarget_modes RetargetMode.Off
              shift_right_click_action = mode_field mouse_gesture_actions MouseGestureAction.Off
              alt_right_click_action = mode_field mouse_gesture_actions MouseGestureAction.Off
              ctrl_right_click_action = mode_field mouse_gesture_actions MouseGestureAction.Off
              middle_mouse_action = mode_field mouse_gesture_actions MouseGestureAction.Off
              mouse4_action = mode_field mouse_gesture_actions MouseGestureAction.Off
              mouse5_action = mode_field mouse_gesture_actions MouseGestureAction.Off
              viewport_paint_mode = mode_field paint_modes ViewportPaintMode.Queued }
          options =
            { enabled = new CheckBox(Text = "Enable Rhinos Can Fly")
              normalize_diagonal_movement = new CheckBox(Text = "Normalize diagonal movement")
              hide_gumball_while_flying = new CheckBox(Text = "Hide gumball while flying")
              save_speed_to_document = new CheckBox(Text = "Save current speed to document")
              load_speed_from_document = new CheckBox(Text = "Load speed from document")
              wheel_changes_speed_during_flight_navigation =
                new CheckBox(Text = "MWheel changes speed during pan/pivot")
              mouse4_action_while_flying = new CheckBox(Text = "Also while flying")
              mouse5_action_while_flying = new CheckBox(Text = "Also while flying")
              middle_mouse_action_while_flying = new CheckBox(Text = "Also while flying")
              middle_mouse_uses_cursor_outside_flight = new CheckBox(Text = "Use cursor position outside flight")
              mouse4_uses_cursor_outside_flight = new CheckBox(Text = "Use cursor position outside flight")
              mouse5_uses_cursor_outside_flight = new CheckBox(Text = "Use cursor position outside flight")
              exit_on_mouse_left = new CheckBox(Text = "Left click exits flight / navigation")
              exit_on_mouse_right = new CheckBox(Text = "Right click exits flight / navigation")
              exit_on_mouse_middle = new CheckBox(Text = "Middle mouse exits flight")
              exit_on_mouse4 = new CheckBox(Text = "Mouse 4 exits flight")
              exit_on_mouse5 = new CheckBox(Text = "Mouse 5 exits flight")
              commands_do_not_repeat = new CheckBox(Text = "Don't repeat flight commands") }
          viewport_capability_names = new TextBox()
          right_click_flight_entry_names = new TextBox() }
      status =
        { runtime_enabled = new CheckBox(Text = "Runtime enabled (RhinosCanFlyToggleEnable)", Enabled = false)
          status_line = new Label(Wrap = WrapMode.Word)
          runtime_line = new Label(Wrap = WrapMode.Word) }
      raw_json =
        { path = new TextBox(ReadOnly = true)
          contents = new TextArea(ReadOnly = true, Wrap = false, Height = 132) }
      actions =
        { reset_all = new Button(Text = "Reset all to defaults")
          raw_json_toggle = new Button(Text = "Show raw JSON configuration")
          github = new Button(Text = "GitHub: Viterkim/RhinosCanFly") } }
