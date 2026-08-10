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
      pivot_left: TextBox
      pivot_right: TextBox
      pivot_toggle: TextBox
      pivot_hold: TextBox
      boost: TextBox
      slow: TextBox
      speed_increase: TextBox
      speed_decrease: TextBox
      exit_key: TextBox }

type NumberFields =
    { base_speed: TextBox
      minimum_speed: TextBox
      maximum_speed: TextBox
      speed_step_multiplier: TextBox
      boost_multiplier: TextBox
      slow_multiplier: TextBox
      vertical_speed_multiplier: TextBox
      pivot_speed_multiplier: TextBox
      mouse_pivot_multiplier: TextBox
      mouse_sensitivity: TextBox
      forced_lens_length_mm: TextBox
      lens_length_delta_mm: TextBox }

type ModeFields =
    { boost_mode: ModeField<KeyActivationMode>
      slow_mode: ModeField<KeyActivationMode>
      wheel_speed_mode: ModeField<MouseWheelSpeedMode>
      mouse_x_mode: ModeField<MouseAxisMode>
      mouse_y_mode: ModeField<MouseAxisMode>
      right_click_entry_mode: ModeField<RightClickEntryMode>
      shift_right_click_mode: ModeField<ModifiedRightClickMode>
      alt_right_click_mode: ModeField<ModifiedRightClickMode>
      mouse4_pivot_mode: ModeField<MouseButtonPivotMode>
      mouse5_pivot_mode: ModeField<MouseButtonPivotMode>
      middle_mouse_while_flying: ModeField<FlyingMiddleMouseMode>
      viewport_redraw_mode: ModeField<ViewportRedrawMode> }

type OptionFields =
    { enabled: CheckBox
      normalize_diagonal_movement: CheckBox
      hide_gumball_while_flying: CheckBox
      pivot_bindings_ignore_gumball: CheckBox
      save_speed_to_document: CheckBox
      load_speed_from_document: CheckBox
      exit_on_mouse_left: CheckBox
      exit_on_mouse_right: CheckBox
      mouse4_also_while_flying: CheckBox
      mouse5_also_while_flying: CheckBox
      commands_do_not_repeat: CheckBox }

type ConfigFields =
    { bindings: BindingFields
      numbers: NumberFields
      modes: ModeFields
      options: OptionFields }

type StatusFields =
    { status_line: Label
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

let text_box () = new TextBox(Height = 24)

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
    field.control.SelectedIndex <-
        field.options
        |> Array.tryFindIndex (fun (candidate: 'Mode, _: string) -> candidate = value)
        |> Option.defaultValue 0

let create () =
    let activationModes =
        [| KeyActivationMode.Toggle, "Toggle"; KeyActivationMode.Hold, "Hold" |]

    let mouseAxisModes =
        [| MouseAxisMode.Normal, "Normal"; MouseAxisMode.Inverted, "Inverted" |]

    let wheelSpeedModes =
        [| MouseWheelSpeedMode.Off, "Off"
           MouseWheelSpeedMode.Normal, "On"
           MouseWheelSpeedMode.Reversed, "On but reversed" |]

    let mousePivotModes =
        [| MouseButtonPivotMode.Off, "Off"
           MouseButtonPivotMode.Hold, "Hold to pivot"
           MouseButtonPivotMode.Toggle, "Toggle pivot" |]

    let flyingMiddleMouseModes =
        [| FlyingMiddleMouseMode.Off, "Off"
           FlyingMiddleMouseMode.ExitFlying, "Exit"
           FlyingMiddleMouseMode.TogglePivot, "Toggle pivot" |]

    let modifiedRightClickModes =
        [| ModifiedRightClickMode.Off, "Off"
           ModifiedRightClickMode.Pivot, "Pivot"
           ModifiedRightClickMode.Pan, "Pan" |]

    let rightClickEntryModes =
        [| RightClickEntryMode.Off, "Off"
           RightClickEntryMode.EnterFlying, "Enter flying"
           RightClickEntryMode.EnterFlyingDuringCommands, "Enter flying + during cmds"
           RightClickEntryMode.EnterFlyingWhileHeld, "Held enters flying"
           RightClickEntryMode.EnterFlyingWhileHeldDuringCommands, "Held enters flying + during cmds" |]

    let redrawModes =
        [| ViewportRedrawMode.Rhino, "Rhino redraw (default)"
           ViewportRedrawMode.RhinoImmediate, "Rhino redraw with immediate paint"
           ViewportRedrawMode.NativeWindow, "Native window redraw (experimental)" |]

    { config =
        { bindings =
            { forward = text_box ()
              backward = text_box ()
              left = text_box ()
              right = text_box ()
              up = text_box ()
              down = text_box ()
              pivot_left = text_box ()
              pivot_right = text_box ()
              pivot_toggle = text_box ()
              pivot_hold = text_box ()
              boost = text_box ()
              slow = text_box ()
              speed_increase = text_box ()
              speed_decrease = text_box ()
              exit_key = text_box () }
          numbers =
            { base_speed = text_box ()
              minimum_speed = text_box ()
              maximum_speed = text_box ()
              speed_step_multiplier = text_box ()
              boost_multiplier = text_box ()
              slow_multiplier = text_box ()
              vertical_speed_multiplier = text_box ()
              pivot_speed_multiplier = text_box ()
              mouse_pivot_multiplier = text_box ()
              mouse_sensitivity = text_box ()
              forced_lens_length_mm = text_box ()
              lens_length_delta_mm = text_box () }
          modes =
            { boost_mode = mode_field activationModes KeyActivationMode.Toggle
              slow_mode = mode_field activationModes KeyActivationMode.Toggle
              wheel_speed_mode = mode_field wheelSpeedModes MouseWheelSpeedMode.Normal
              mouse_x_mode = mode_field mouseAxisModes MouseAxisMode.Normal
              mouse_y_mode = mode_field mouseAxisModes MouseAxisMode.Normal
              right_click_entry_mode = mode_field rightClickEntryModes RightClickEntryMode.EnterFlyingDuringCommands
              shift_right_click_mode = mode_field modifiedRightClickModes ModifiedRightClickMode.Off
              alt_right_click_mode = mode_field modifiedRightClickModes ModifiedRightClickMode.Off
              mouse4_pivot_mode = mode_field mousePivotModes MouseButtonPivotMode.Off
              mouse5_pivot_mode = mode_field mousePivotModes MouseButtonPivotMode.Off
              middle_mouse_while_flying = mode_field flyingMiddleMouseModes FlyingMiddleMouseMode.Off
              viewport_redraw_mode = mode_field redrawModes ViewportRedrawMode.Rhino }
          options =
            { enabled = new CheckBox(Text = "Enable Rhinos Can Fly")
              normalize_diagonal_movement = new CheckBox(Text = "Normalize diagonal movement")
              hide_gumball_while_flying = new CheckBox(Text = "Hide gumball while flying")
              pivot_bindings_ignore_gumball = new CheckBox(Text = "Pivot binds don't rotate around gumball")
              save_speed_to_document = new CheckBox(Text = "Save current speed to file")
              load_speed_from_document = new CheckBox(Text = "Load speed from file")
              exit_on_mouse_left = new CheckBox(Text = "Left click exits")
              exit_on_mouse_right = new CheckBox(Text = "Right click exits")
              mouse4_also_while_flying = new CheckBox(Text = "Mouse 4 also while flying")
              mouse5_also_while_flying = new CheckBox(Text = "Mouse 5 also while flying")
              commands_do_not_repeat = new CheckBox(Text = "Don't count fly command as repeatable") } }
      status =
        { status_line = new Label(Wrap = WrapMode.Word)
          runtime_line = new Label(Wrap = WrapMode.Word) }
      raw_json =
        { path = new TextBox(ReadOnly = true)
          contents = new TextArea(ReadOnly = true, Wrap = false, Height = 132) }
      actions =
        { reset_all = new Button(Text = "Reset all to defaults")
          raw_json_toggle = new Button(Text = "Show raw JSON configuration")
          github = new Button(Text = "GitHub: Viterkim/RhinosCanFly") } }
