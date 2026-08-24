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
      parallel_view_flying: ModeField<ParallelViewFlyingMode>
      right_click_entry_mode: ModeField<RightClickEntryMode>
      default_flight_mode: ModeField<DefaultFlightMode>
      shift_right_click_retarget: ModeField<RetargetMode>
      alt_right_click_retarget: ModeField<RetargetMode>
      ctrl_right_click_retarget: ModeField<RetargetMode>
      middle_mouse_retarget: ModeField<RetargetMode>
      mouse4_retarget: ModeField<RetargetMode>
      mouse5_retarget: ModeField<RetargetMode>
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
      flight_pivot_uses_gumball: CheckBox
      save_speed_to_document: CheckBox
      load_speed_from_document: CheckBox
      wheel_changes_speed_during_flight_navigation: CheckBox
      mouse4_action_while_flying: CheckBox
      mouse5_action_while_flying: CheckBox
      middle_mouse_action_while_flying: CheckBox
      middle_mouse_uses_cursor_outside_flight: CheckBox
      mouse4_uses_cursor_outside_flight: CheckBox
      mouse5_uses_cursor_outside_flight: CheckBox
      right_click_enters_parallel_views: CheckBox
      exit_on_mouse_left: CheckBox
      exit_on_mouse_right: CheckBox
      commands_do_not_repeat: CheckBox }

type ConfigFields =
    { bindings: BindingFields
      numbers: NumberFields
      modes: ModeFields
      options: OptionFields
      parallel_view_names: TextBox }

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

let text_box () = new TextBox()

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
    let selectedIndex =
        match
            field.options
            |> Array.tryFindIndex (fun (candidate: 'Mode, _: string) -> candidate = value)
        with
        | Some index -> index
        | None ->
            field.options
            |> Array.tryFindIndex (fun (candidate: 'Mode, _: string) -> candidate = field.fallback)
            |> Option.defaultValue 0

    field.control.SelectedIndex <- selectedIndex

let create () =
    let activationModes =
        [| KeyActivationMode.Toggle, "Toggle"; KeyActivationMode.Hold, "Hold" |]

    let mouseAxisModes =
        [| MouseAxisMode.Normal, "Normal"; MouseAxisMode.Inverted, "Inverted" |]

    let wheelSpeedModes =
        [| MouseWheelSpeedMode.Off, "Off"
           MouseWheelSpeedMode.Normal, "On"
           MouseWheelSpeedMode.Reversed, "On but reversed" |]

    let mouseGestureActions =
        [| MouseGestureAction.Off, "Off"
           MouseGestureAction.TogglePivot, "Toggle pivot"
           MouseGestureAction.HoldPivot, "Hold pivot"
           MouseGestureAction.TogglePan, "Toggle pan"
           MouseGestureAction.HoldPan, "Hold pan"
           MouseGestureAction.Retarget, "Retarget" |]

    let rightClickEntryModes =
        [| RightClickEntryMode.Off, "Off"
           RightClickEntryMode.ClickToFly, "Click to fly"
           RightClickEntryMode.ClickToFlyDuringCommands, "Click to fly + during commands"
           RightClickEntryMode.HoldToFly, "Hold to fly"
           RightClickEntryMode.HoldToFlyDuringCommands, "Hold to fly + during commands" |]

    let parallelViewFlyingModes =
        [| ParallelViewFlyingMode.DisabledAll, "Off"
           ParallelViewFlyingMode.EnabledAll, "Allow all"
           ParallelViewFlyingMode.EnabledSome, "Allow listed"
           ParallelViewFlyingMode.DisabledSome, "Ban listed" |]

    let flightModes =
        [| DefaultFlightMode.Normal, "Normal"
           DefaultFlightMode.Temporary, "Temporary"
           DefaultFlightMode.TemporaryIncludingNavigationCommands, "Temporary, including Pivot/Pan commands" |]

    let retargetModes =
        [| RetargetMode.Off, "Off"
           RetargetMode.Distance, "Distance"
           RetargetMode.GeometryThenDistance, "Geometry, then distance"
           RetargetMode.Geometry, "Geometry, no fallback"
           RetargetMode.TargetThenDistance, "Target, then distance"
           RetargetMode.Target, "Target, no fallback"
           RetargetMode.ObjectCenterThenDistance, "Object center, then distance"
           RetargetMode.ObjectCenter, "Object center, no fallback" |]

    let paintModes =
        [| ViewportPaintMode.Queued, "Normal Rhino redraw (default)"
           ViewportPaintMode.Immediate, "Immediate paint" |]

    { config =
        { bindings =
            { forward = text_box ()
              backward = text_box ()
              left = text_box ()
              right = text_box ()
              up = text_box ()
              down = text_box ()
              key_pivot_left = text_box ()
              key_pivot_right = text_box ()
              pivot_toggle = text_box ()
              pivot_hold = text_box ()
              pan_toggle = text_box ()
              pan_hold = text_box ()
              boost = text_box ()
              slow = text_box ()
              speed_increase = text_box ()
              speed_decrease = text_box ()
              exit_key = text_box ()
              cancel_flight_and_restore = text_box ()
              toggle_projection = text_box () }
          numbers =
            { base_speed = text_box ()
              minimum_speed = text_box ()
              maximum_speed = text_box ()
              speed_step_multiplier = text_box ()
              boost_multiplier = text_box ()
              slow_multiplier = text_box ()
              vertical_speed_multiplier = text_box ()
              key_pivot_speed_multiplier = text_box ()
              mouse_pivot_multiplier = text_box ()
              mouse_pan_multiplier = text_box ()
              perspective_retarget_fallback_multiplier = text_box ()
              parallel_retarget_fallback_multiplier = text_box ()
              perspective_retarget_zoom_border = text_box ()
              parallel_retarget_zoom_border = text_box ()
              parallel_mouse_sensitivity = text_box ()
              parallel_mouse_pivot_multiplier = text_box ()
              parallel_mouse_pan_multiplier = text_box ()
              parallel_zoom_speed_multiplier = text_box ()
              parallel_up_down_multiplier = text_box ()
              mouse_sensitivity = text_box ()
              perspective_lens_length_after_parallel_mm = text_box ()
              forced_perspective_lens_length_on_flight_start_mm = text_box ()
              perspective_lens_length_delta_during_flight_mm = text_box () }
          modes =
            { boost_mode = mode_field activationModes KeyActivationMode.Toggle
              slow_mode = mode_field activationModes KeyActivationMode.Toggle
              wheel_speed_mode = mode_field wheelSpeedModes MouseWheelSpeedMode.Normal
              mouse_x_mode = mode_field mouseAxisModes MouseAxisMode.Normal
              mouse_y_mode = mode_field mouseAxisModes MouseAxisMode.Normal
              parallel_view_flying = mode_field parallelViewFlyingModes ParallelViewFlyingMode.DisabledAll
              right_click_entry_mode = mode_field rightClickEntryModes RightClickEntryMode.ClickToFlyDuringCommands
              default_flight_mode = mode_field flightModes DefaultFlightMode.Normal
              shift_right_click_retarget = mode_field retargetModes RetargetMode.ObjectCenter
              alt_right_click_retarget = mode_field retargetModes RetargetMode.ObjectCenter
              ctrl_right_click_retarget = mode_field retargetModes RetargetMode.ObjectCenter
              middle_mouse_retarget = mode_field retargetModes RetargetMode.ObjectCenter
              mouse4_retarget = mode_field retargetModes RetargetMode.ObjectCenter
              mouse5_retarget = mode_field retargetModes RetargetMode.ObjectCenter
              retarget_on_pivot = mode_field retargetModes RetargetMode.ObjectCenter
              retarget_on_pan = mode_field retargetModes RetargetMode.ObjectCenter
              retarget_on_flight_exit = mode_field retargetModes RetargetMode.ObjectCenter
              retarget_on_restored_flight_exit = mode_field retargetModes RetargetMode.Off
              shift_right_click_action = mode_field mouseGestureActions MouseGestureAction.Off
              alt_right_click_action = mode_field mouseGestureActions MouseGestureAction.Off
              ctrl_right_click_action = mode_field mouseGestureActions MouseGestureAction.Off
              middle_mouse_action = mode_field mouseGestureActions MouseGestureAction.Off
              mouse4_action = mode_field mouseGestureActions MouseGestureAction.Off
              mouse5_action = mode_field mouseGestureActions MouseGestureAction.Off
              viewport_paint_mode = mode_field paintModes ViewportPaintMode.Queued }
          options =
            { enabled = new CheckBox(Text = "Enable Rhinos Can Fly")
              normalize_diagonal_movement = new CheckBox(Text = "Normalize diagonal movement")
              hide_gumball_while_flying = new CheckBox(Text = "Hide gumball while flying")
              flight_pivot_uses_gumball = new CheckBox(Text = "Use gumball as flight pivot target")
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
              right_click_enters_parallel_views = new CheckBox(Text = "Enable right click to enter in parallel")
              exit_on_mouse_left = new CheckBox(Text = "Left click exits flight / navigation")
              exit_on_mouse_right = new CheckBox(Text = "Right click exits flight / navigation")
              commands_do_not_repeat = new CheckBox(Text = "Don't repeat flight commands") }
          parallel_view_names = text_box () }
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
