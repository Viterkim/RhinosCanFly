namespace RhinosCanFly

open System
open System.Diagnostics
open System.Globalization
open System.Reflection
open Eto.Drawing
open Eto.Forms
open Rhino

type SettingsControl() as self =
    inherit Panel()

    let text_box () = new TextBox(Height = 24)

    let defaults = ConfigSchema.defaults

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

    let mode_dropdown (modes: ('Mode * string) array) =
        let control = new DropDown(Height = 24)

        for _, label in modes do
            control.Items.Add label

        control

    let mode_index (value: 'Mode) (modes: ('Mode * string) array) =
        modes
        |> Array.tryFindIndex (fun (candidate: 'Mode, _: string) -> candidate = value)
        |> Option.defaultValue 0

    let selected_mode (control: DropDown) (modes: ('Mode * string) array) (fallback: 'Mode) =
        let index = control.SelectedIndex

        if index >= 0 && index < modes.Length then
            fst modes[index]
        else
            fallback

    let forward = text_box ()
    let backward = text_box ()
    let left = text_box ()
    let right = text_box ()
    let up = text_box ()
    let down = text_box ()
    let pivotLeft = text_box ()
    let pivotRight = text_box ()
    let pivotToggle = text_box ()
    let pivotHold = text_box ()
    let boost = text_box ()
    let slow = text_box ()
    let speedIncrease = text_box ()
    let speedDecrease = text_box ()
    let exitKey = text_box ()
    let baseSpeed = text_box ()
    let minimumSpeed = text_box ()
    let maximumSpeed = text_box ()
    let speedStep = text_box ()
    let boostMultiplier = text_box ()
    let slowMultiplier = text_box ()
    let verticalSpeedMultiplier = text_box ()
    let pivotSpeedMultiplier = text_box ()
    let mousePivotMultiplier = text_box ()
    let mouseSensitivity = text_box ()
    let forcedLensLength = text_box ()
    let lensLengthDelta = text_box ()

    let boostMode = mode_dropdown activationModes
    let slowMode = mode_dropdown activationModes
    let wheelSpeedMode = mode_dropdown wheelSpeedModes
    let mouseXMode = mode_dropdown mouseAxisModes
    let mouseYMode = mode_dropdown mouseAxisModes
    let rightClickEntryMode = mode_dropdown rightClickEntryModes
    let shiftRightClickMode = mode_dropdown modifiedRightClickModes
    let altRightClickMode = mode_dropdown modifiedRightClickModes
    let mouse4PivotMode = mode_dropdown mousePivotModes
    let mouse5PivotMode = mode_dropdown mousePivotModes
    let flyingMiddleMouseMode = mode_dropdown flyingMiddleMouseModes
    let viewportRedrawMode = mode_dropdown redrawModes

    let pluginEnabled = new CheckBox(Text = "Enable Rhinos Can Fly")
    let normalizeDiagonal = new CheckBox(Text = "Normalize diagonal movement")
    let hideGumball = new CheckBox(Text = "Hide gumball while flying")

    let pivotBindingsIgnoreGumball =
        new CheckBox(Text = "Pivot binds don't rotate around gumball")

    let saveSpeedToDocument = new CheckBox(Text = "Save current speed to file")
    let loadSpeedFromDocument = new CheckBox(Text = "Load speed from file")
    let exitOnMouseLeft = new CheckBox(Text = "Left click exits")
    let exitOnMouseRight = new CheckBox(Text = "Right click exits")

    let mouse4AlsoWhileFlying = new CheckBox(Text = "Mouse 4 also while flying")

    let mouse5AlsoWhileFlying = new CheckBox(Text = "Mouse 5 also while flying")

    let commandsDoNotRepeat =
        new CheckBox(Text = "Don't count fly command as repeatable")

    let statusLine = new Label(Wrap = WrapMode.Word)
    let runtimeLine = new Label(Wrap = WrapMode.Word)
    let configPath = new TextBox(ReadOnly = true)

    let rawJson = new TextArea(ReadOnly = true, Wrap = false, Height = 132)

    let resetAll = new Button(Text = "Reset all to defaults")
    let rawJsonToggle = new Button(Text = "Show raw JSON configuration")
    let github = new Button(Text = "GitHub: Viterkim/RhinosCanFly")
    let version = Assembly.GetExecutingAssembly().GetName().Version
    let versionText = $"{version.Major}.{version.Minor}.{version.Build}"
    let mutable configurationError: string option = None
    let mutable speedText = "Unavailable"
    let mutable lensText = "Unavailable"
    let optionsIcon = SettingsUi.load_icon ()
    let bindingCapture = BindingCapture.create ()
    let mutable resourcesDisposed = false

    let format_runtime_number (value: float) =
        value.ToString("0.######", CultureInfo.InvariantCulture)

    let is_checked (control: CheckBox) = control.Checked.GetValueOrDefault()

    let set_checked (control: CheckBox) (value: bool) = control.Checked <- Nullable value

    let refresh_status () =
        statusLine.Text <-
            match configurationError with
            | Some error -> $"Version: {versionText}  |  Configuration error: {error}"
            | None -> $"Version: {versionText}"

        runtimeLine.Text <- $"Current Speed: {speedText}  |  Current Lens: {lensText}"

    let refresh_side_button_flight_controls () =
        mouse4AlsoWhileFlying.Enabled <-
            selected_mode mouse4PivotMode mousePivotModes MouseButtonPivotMode.Off
            <> MouseButtonPivotMode.Off

        mouse5AlsoWhileFlying.Enabled <-
            selected_mode mouse5PivotMode mousePivotModes MouseButtonPivotMode.Off
            <> MouseButtonPivotMode.Off

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

    let binding_editor (field: TextBox) (defaultValue: string) =
        BindingCapture.editor bindingCapture field defaultValue

    let title_row () =
        let image = new ImageView(Size = Size(32, 32))

        optionsIcon |> Option.iter (fun (icon: Icon) -> image.Image <- icon)

        let title =
            new Label(Text = "Rhinos Can Fly Options", Font = SystemFonts.Bold(Nullable 15.f, FontDecoration.None))

        let row = new TableLayout(Spacing = Size(10, 0))

        row.Rows.Add(SettingsLayout.row [ new TableCell(image, false); new TableCell(title, true) ])

        row :> Control

    let rawJsonLayout =
        let layout = new TableLayout(Spacing = Size(8, 6))
        let pathLabel = new Label(Text = "File", Width = 64)
        let contentsLabel = new Label(Text = "Contents", Width = 64)

        layout.Rows.Add(SettingsLayout.row [ new TableCell(pathLabel, false); new TableCell(configPath, true) ])

        layout.Rows.Add(SettingsLayout.row [ new TableCell(contentsLabel, false); new TableCell(rawJson, true) ])

        layout

    let rawJsonPanel = new Panel(Content = rawJsonLayout, Visible = false)

    let refresh_raw () =
        match ConfigStorage.read_raw () with
        | Ok(path, content) ->
            configPath.Text <- path
            rawJson.Text <- content
        | Error error ->
            configPath.Text <- ConfigStorage.path ()
            rawJson.Text <- $"Could not read config: {error}"

    let refresh_raw_if_visible () =
        if rawJsonPanel.Visible then
            refresh_raw ()

    let mainTable = new TableLayout(Padding = Padding 12, Spacing = Size(0, 4))

    do
        mainTable.Rows.Add(SettingsLayout.full_width (title_row ()))
        mainTable.Rows.Add(SettingsLayout.full_width (SettingsLayout.heading "General behaviour"))

        SettingsLayout.grid
            2
            [ pluginEnabled
              commandsDoNotRepeat
              saveSpeedToDocument
              loadSpeedFromDocument
              normalizeDiagonal ]
        |> SettingsLayout.full_width
        |> mainTable.Rows.Add

        mainTable.Rows.Add(SettingsLayout.full_width (SettingsLayout.heading "Non flying behaviour"))

        SettingsLayout.grid
            2
            [ SettingsLayout.item "Right click" rightClickEntryMode
              SettingsLayout.item "Shift + right click" shiftRightClickMode
              SettingsLayout.item "Alt + right click" altRightClickMode ]
        |> SettingsLayout.full_width
        |> mainTable.Rows.Add

        mainTable.Rows.Add(
            SettingsLayout.full_width (
                SettingsLayout.note
                    "Pivot and pan require Options -> Mouse -> Middle mouse button to be set to Manipulate view with Rotate."
            )
        )

        mainTable.Rows.Add(SettingsLayout.full_width (SettingsLayout.heading "While flying behaviour"))

        SettingsLayout.grid 2 [ exitOnMouseRight; exitOnMouseLeft; hideGumball; pivotBindingsIgnoreGumball ]
        |> SettingsLayout.full_width
        |> mainTable.Rows.Add

        mainTable.Rows.Add(SettingsLayout.full_width (SettingsLayout.spacer 2))

        SettingsLayout.grid
            2
            [ SettingsLayout.item "Mouse X" mouseXMode
              SettingsLayout.item "Mouse Y" mouseYMode
              SettingsLayout.item "Middle mouse" flyingMiddleMouseMode
              SettingsLayout.item "Mouse sensitivity" mouseSensitivity ]
        |> SettingsLayout.full_width
        |> mainTable.Rows.Add

        mainTable.Rows.Add(SettingsLayout.full_width (SettingsLayout.heading "Mouse 4/5 behaviour"))

        SettingsLayout.grid
            2
            [ SettingsLayout.item "Mouse 4" mouse4PivotMode
              mouse4AlsoWhileFlying
              SettingsLayout.item "Mouse 5" mouse5PivotMode
              mouse5AlsoWhileFlying ]
        |> SettingsLayout.full_width
        |> mainTable.Rows.Add

        mainTable.Rows.Add(SettingsLayout.full_width (SettingsLayout.spacer 2))

        mainTable.Rows.Add(
            SettingsLayout.full_width (
                SettingsLayout.note
                    "Toggle pivots stop with the same button. While flying uses the same Hold or Toggle mode when enabled."
            )
        )

        mainTable.Rows.Add(SettingsLayout.full_width (SettingsLayout.heading "Controls"))

        SettingsLayout.grid
            2
            [ SettingsLayout.item "Forward" (binding_editor forward defaults.forward)
              SettingsLayout.item "Backward" (binding_editor backward defaults.backward)
              SettingsLayout.item "Move left" (binding_editor left defaults.left)
              SettingsLayout.item "Move right" (binding_editor right defaults.right)
              SettingsLayout.item "Move up" (binding_editor up defaults.up)
              SettingsLayout.item "Move down" (binding_editor down defaults.down)
              SettingsLayout.item "Pivot left" (binding_editor pivotLeft defaults.pivot_left)
              SettingsLayout.item "Pivot right" (binding_editor pivotRight defaults.pivot_right)
              SettingsLayout.item "Toggle pivot" (binding_editor pivotToggle defaults.pivot_toggle)
              SettingsLayout.item "Hold pivot" (binding_editor pivotHold defaults.pivot_hold)
              SettingsLayout.item "Exit" (binding_editor exitKey defaults.exit_key) ]
        |> SettingsLayout.full_width
        |> mainTable.Rows.Add

        mainTable.Rows.Add(SettingsLayout.full_width (SettingsLayout.heading "Movement speed and controls"))
        mainTable.Rows.Add(SettingsLayout.full_width (SettingsLayout.subheading "Options"))

        SettingsLayout.grid
            2
            [ SettingsLayout.item "Boost behaviour" boostMode
              SettingsLayout.item "Slow behaviour" slowMode
              SettingsLayout.item "MWheel changes speed" wheelSpeedMode ]
        |> SettingsLayout.full_width
        |> mainTable.Rows.Add

        mainTable.Rows.Add(SettingsLayout.full_width (SettingsLayout.subheading "Binds"))

        SettingsLayout.grid
            2
            [ SettingsLayout.item "Boost mode" (binding_editor boost defaults.boost)
              SettingsLayout.item "Slow mode" (binding_editor slow defaults.slow)
              SettingsLayout.item "Increase speed" (binding_editor speedIncrease defaults.speed_increase)
              SettingsLayout.item "Decrease speed" (binding_editor speedDecrease defaults.speed_decrease) ]
        |> SettingsLayout.full_width
        |> mainTable.Rows.Add

        mainTable.Rows.Add(SettingsLayout.full_width (SettingsLayout.subheading "Values"))

        SettingsLayout.grid
            2
            [ SettingsLayout.item "Base speed" baseSpeed
              SettingsLayout.item "Minimum speed" minimumSpeed
              SettingsLayout.item "Maximum speed" maximumSpeed
              SettingsLayout.item "Speed step multiplier" speedStep
              SettingsLayout.item "Boost multiplier" boostMultiplier
              SettingsLayout.item "Slow multiplier" slowMultiplier
              SettingsLayout.item "Move up/down multiplier" verticalSpeedMultiplier
              SettingsLayout.item "Key pivot speed multi" pivotSpeedMultiplier
              SettingsLayout.item "Fly + pivot sens multi" mousePivotMultiplier ]
        |> SettingsLayout.full_width
        |> mainTable.Rows.Add

        mainTable.Rows.Add(SettingsLayout.full_width (SettingsLayout.heading "Viewport"))

        SettingsLayout.grid
            2
            [ SettingsLayout.item "Force lens length" forcedLensLength
              SettingsLayout.item "Force lens length delta" lensLengthDelta ]
        |> SettingsLayout.full_width
        |> mainTable.Rows.Add

        mainTable.Rows.Add(SettingsLayout.full_width (SettingsLayout.item "Redraw mode" viewportRedrawMode))

        mainTable.Rows.Add(
            SettingsLayout.full_width (SettingsLayout.note "Try an alternative mode only if fly mode is not smooth.")
        )

        mainTable.Rows.Add(SettingsLayout.full_width (SettingsLayout.heading "Status"))
        refresh_status ()
        mainTable.Rows.Add(SettingsLayout.full_width statusLine)
        mainTable.Rows.Add(SettingsLayout.full_width runtimeLine)
        mainTable.Rows.Add(SettingsLayout.full_width github)
        mainTable.Rows.Add(SettingsLayout.full_width resetAll)
        mainTable.Rows.Add(SettingsLayout.full_width rawJsonToggle)
        mainTable.Rows.Add(SettingsLayout.full_width rawJsonPanel)

        let scrollable =
            new Scrollable(Border = BorderType.None, ExpandContentWidth = true, Content = mainTable)

        let host = new TableLayout(Spacing = Size.Empty)
        host.Rows.Add(SettingsLayout.row [ new TableCell(bindingCapture.focus_sink, false) ])

        let contentRow = SettingsLayout.row [ new TableCell(scrollable, true) ]
        contentRow.ScaleHeight <- true
        host.Rows.Add contentRow

        self.Content <- host

        mouse4PivotMode.SelectedIndexChanged.Add(fun (_: EventArgs) -> refresh_side_button_flight_controls ())
        mouse5PivotMode.SelectedIndexChanged.Add(fun (_: EventArgs) -> refresh_side_button_flight_controls ())

        refresh_side_button_flight_controls ()

        rawJsonToggle.Click.Add(fun (_: EventArgs) ->
            rawJsonPanel.Visible <- not rawJsonPanel.Visible

            refresh_raw_if_visible ()

            rawJsonToggle.Text <-
                if rawJsonPanel.Visible then
                    "Hide raw JSON configuration"
                else
                    "Show raw JSON configuration")

        github.Click.Add(fun (_: EventArgs) ->
            try
                let launchedProcess =
                    Process.Start(ProcessStartInfo("https://github.com/Viterkim/RhinosCanFly", UseShellExecute = true))

                if not (isNull launchedProcess) then
                    launchedProcess.Dispose()
            with error ->
                self.ShowError $"Could not open GitHub: {error.Message}")

        resetAll.Click.Add(fun (_: EventArgs) ->
            self.LoadConfig defaults

            match RuntimeSettings.save_apply_and_set_speed RhinoDoc.ActiveDoc defaults defaults.base_speed with
            | Ok _ ->
                speedText <- ConfigSchema.format_number defaults.base_speed
                refresh_status ()
                self.ClearError()
            | Error error -> self.ShowError error

            refresh_raw_if_visible ())

        BindingCapture.attach_mouse_behavior bindingCapture self
        SettingsUi.use_rhino_style self
        self.UnLoad.Add(fun (_: EventArgs) -> BindingCapture.cancel bindingCapture)

    override _.Dispose(disposing: bool) =
        if disposing && not resourcesDisposed then
            resourcesDisposed <- true
            BindingCapture.dispose bindingCapture
            optionsIcon |> Option.iter (fun (icon: Icon) -> icon.Dispose())

        base.Dispose disposing

    member _.ShowError(message: string) =
        configurationError <- Some message
        refresh_status ()

    member _.ClearError() =
        configurationError <- None
        refresh_status ()

    member _.ShowRuntimeState(speed: float, lens: float option) =
        speedText <- format_runtime_number speed

        lensText <-
            match lens with
            | Some value -> $"{format_runtime_number value} mm"
            | None -> "Unavailable"

        refresh_status ()

    member _.RefreshRawIfVisible() = refresh_raw_if_visible ()

    member _.LoadConfig(config: FlyConfigFile) =
        set_checked pluginEnabled config.enabled
        forward.Text <- config.forward
        backward.Text <- config.backward
        left.Text <- config.left
        right.Text <- config.right
        up.Text <- config.up
        down.Text <- config.down
        pivotLeft.Text <- config.pivot_left
        pivotRight.Text <- config.pivot_right
        pivotToggle.Text <- config.pivot_toggle
        pivotHold.Text <- config.pivot_hold
        boost.Text <- config.boost
        slow.Text <- config.slow
        speedIncrease.Text <- config.speed_increase
        speedDecrease.Text <- config.speed_decrease
        exitKey.Text <- config.exit_key
        baseSpeed.Text <- ConfigSchema.format_number config.base_speed
        minimumSpeed.Text <- ConfigSchema.format_number config.minimum_speed
        maximumSpeed.Text <- ConfigSchema.format_number config.maximum_speed
        speedStep.Text <- ConfigSchema.format_number config.speed_step_multiplier
        boostMultiplier.Text <- ConfigSchema.format_number config.boost_multiplier
        slowMultiplier.Text <- ConfigSchema.format_number config.slow_multiplier
        verticalSpeedMultiplier.Text <- ConfigSchema.format_number config.vertical_speed_multiplier
        pivotSpeedMultiplier.Text <- ConfigSchema.format_number config.pivot_speed_multiplier
        mousePivotMultiplier.Text <- ConfigSchema.format_number config.mouse_pivot_multiplier
        mouseSensitivity.Text <- ConfigSchema.format_number config.mouse_sensitivity
        forcedLensLength.Text <- ConfigSchema.format_number config.forced_lens_length_mm
        lensLengthDelta.Text <- ConfigSchema.format_number config.lens_length_delta_mm
        viewportRedrawMode.SelectedIndex <- mode_index config.viewport_redraw_mode redrawModes
        boostMode.SelectedIndex <- mode_index config.boost_mode activationModes
        slowMode.SelectedIndex <- mode_index config.slow_mode activationModes
        wheelSpeedMode.SelectedIndex <- mode_index config.wheel_speed_mode wheelSpeedModes
        rightClickEntryMode.SelectedIndex <- mode_index config.right_click_entry_mode rightClickEntryModes
        shiftRightClickMode.SelectedIndex <- mode_index config.shift_right_click_mode modifiedRightClickModes
        altRightClickMode.SelectedIndex <- mode_index config.alt_right_click_mode modifiedRightClickModes
        mouse4PivotMode.SelectedIndex <- mode_index config.mouse4_pivot_mode mousePivotModes
        mouse5PivotMode.SelectedIndex <- mode_index config.mouse5_pivot_mode mousePivotModes
        flyingMiddleMouseMode.SelectedIndex <- mode_index config.middle_mouse_while_flying flyingMiddleMouseModes
        mouseXMode.SelectedIndex <- mode_index config.mouse_x_mode mouseAxisModes
        mouseYMode.SelectedIndex <- mode_index config.mouse_y_mode mouseAxisModes
        set_checked normalizeDiagonal config.normalize_diagonal_movement
        set_checked hideGumball config.hide_gumball_while_flying
        set_checked pivotBindingsIgnoreGumball config.pivot_bindings_ignore_gumball
        set_checked saveSpeedToDocument config.save_speed_to_document
        set_checked loadSpeedFromDocument config.load_speed_from_document
        set_checked exitOnMouseLeft config.exit_on_mouse_left
        set_checked exitOnMouseRight config.exit_on_mouse_right
        set_checked mouse4AlsoWhileFlying config.mouse4_also_while_flying
        set_checked mouse5AlsoWhileFlying config.mouse5_also_while_flying
        set_checked commandsDoNotRepeat config.commands_do_not_repeat
        refresh_side_button_flight_controls ()

    member _.ReadConfig() =
        match
            parse_number "Base speed" baseSpeed,
            parse_number "Minimum speed" minimumSpeed,
            parse_number "Maximum speed" maximumSpeed,
            parse_number "Speed step multiplier" speedStep,
            parse_number "Boost multiplier" boostMultiplier,
            parse_number "Slow multiplier" slowMultiplier,
            parse_number "Move up/down multiplier" verticalSpeedMultiplier,
            parse_number "Key pivot speed multi" pivotSpeedMultiplier,
            parse_number "Fly + pivot sens multi" mousePivotMultiplier,
            parse_number "Mouse sensitivity" mouseSensitivity,
            parse_optional_number "Force lens length" forcedLensLength,
            parse_optional_number "Force lens length delta" lensLengthDelta
        with
        | Ok baseValue,
          Ok minimumValue,
          Ok maximumValue,
          Ok stepValue,
          Ok boostValue,
          Ok slowValue,
          Ok verticalValue,
          Ok pivotValue,
          Ok mousePivotValue,
          Ok sensitivityValue,
          Ok forcedLensValue,
          Ok lensDeltaValue ->
            Ok
                { config_version = ConfigSchema.current_version
                  enabled = is_checked pluginEnabled
                  forward = forward.Text
                  backward = backward.Text
                  left = left.Text
                  right = right.Text
                  up = up.Text
                  down = down.Text
                  pivot_left = pivotLeft.Text
                  pivot_right = pivotRight.Text
                  pivot_toggle = pivotToggle.Text
                  pivot_hold = pivotHold.Text
                  boost = boost.Text
                  slow = slow.Text
                  speed_increase = speedIncrease.Text
                  speed_decrease = speedDecrease.Text
                  exit_key = exitKey.Text
                  base_speed = baseValue
                  minimum_speed = minimumValue
                  maximum_speed = maximumValue
                  speed_step_multiplier = stepValue
                  boost_multiplier = boostValue
                  slow_multiplier = slowValue
                  pivot_speed_multiplier = pivotValue
                  mouse_pivot_multiplier = mousePivotValue
                  mouse_sensitivity = sensitivityValue
                  mouse_x_mode = selected_mode mouseXMode mouseAxisModes MouseAxisMode.Normal
                  mouse_y_mode = selected_mode mouseYMode mouseAxisModes MouseAxisMode.Normal
                  normalize_diagonal_movement = is_checked normalizeDiagonal
                  hide_gumball_while_flying = is_checked hideGumball
                  pivot_bindings_ignore_gumball = is_checked pivotBindingsIgnoreGumball
                  save_speed_to_document = is_checked saveSpeedToDocument
                  load_speed_from_document = is_checked loadSpeedFromDocument
                  wheel_speed_mode = selected_mode wheelSpeedMode wheelSpeedModes MouseWheelSpeedMode.Normal
                  exit_on_mouse_left = is_checked exitOnMouseLeft
                  exit_on_mouse_right = is_checked exitOnMouseRight
                  middle_mouse_while_flying =
                    selected_mode flyingMiddleMouseMode flyingMiddleMouseModes FlyingMiddleMouseMode.Off
                  mouse4_also_while_flying = is_checked mouse4AlsoWhileFlying
                  mouse5_also_while_flying = is_checked mouse5AlsoWhileFlying
                  right_click_entry_mode =
                    selected_mode rightClickEntryMode rightClickEntryModes RightClickEntryMode.EnterFlyingDuringCommands
                  commands_do_not_repeat = is_checked commandsDoNotRepeat
                  mouse4_pivot_mode = selected_mode mouse4PivotMode mousePivotModes MouseButtonPivotMode.Off
                  mouse5_pivot_mode = selected_mode mouse5PivotMode mousePivotModes MouseButtonPivotMode.Off
                  shift_right_click_mode =
                    selected_mode shiftRightClickMode modifiedRightClickModes ModifiedRightClickMode.Off
                  alt_right_click_mode =
                    selected_mode altRightClickMode modifiedRightClickModes ModifiedRightClickMode.Off
                  boost_mode = selected_mode boostMode activationModes KeyActivationMode.Toggle
                  slow_mode = selected_mode slowMode activationModes KeyActivationMode.Toggle
                  vertical_speed_multiplier = verticalValue
                  forced_lens_length_mm = forcedLensValue
                  lens_length_delta_mm = lensDeltaValue
                  viewport_redraw_mode = selected_mode viewportRedrawMode redrawModes ViewportRedrawMode.Rhino }
        | a, b, c, d, e, f, g, h, i, j, k, l ->
            [ a; b; c; d; e; f; g; h; i; j; k; l ]
            |> List.choose (function
                | Error error -> Some error
                | Ok _ -> None)
            |> String.concat Environment.NewLine
            |> Error
