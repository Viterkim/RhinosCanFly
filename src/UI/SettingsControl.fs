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

    let forward = text_box ()
    let backward = text_box ()
    let left = text_box ()
    let right = text_box ()
    let up = text_box ()
    let down = text_box ()
    let pivotLeft = text_box ()
    let pivotRight = text_box ()
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
    let lensLength = text_box ()

    let redrawModes =
        [| ViewportRedrawMode.Rhino, "Rhino redraw (default)"
           ViewportRedrawMode.RhinoImmediate, "Rhino redraw with immediate paint"
           ViewportRedrawMode.NativeWindow, "Native window redraw (experimental)" |]

    let viewportRedrawMode =
        let control = new DropDown(Height = 24)

        for _, label in redrawModes do
            control.Items.Add label

        control

    let pluginEnabled = new CheckBox(Text = "Enable Rhinos Can Fly")
    let invertMouseX = new CheckBox(Text = "Invert mouse X")
    let invertMouseY = new CheckBox(Text = "Invert mouse Y")
    let normalizeDiagonal = new CheckBox(Text = "Normalize diagonal movement")
    let hideGumball = new CheckBox(Text = "Hide gumball while flying")

    let pivotBindingsIgnoreGumball =
        new CheckBox(Text = "Pivot binds don't rotate around gumball")

    let saveSpeedToDocument = new CheckBox(Text = "Save current speed to file")
    let loadSpeedFromDocument = new CheckBox(Text = "Load speed from file")
    let wheelChangesSpeed = new CheckBox(Text = "Mouse wheel up/down controls speed")
    let exitOnMouseLeft = new CheckBox(Text = "Left mouse button exits fly mode")
    let exitOnMouseRight = new CheckBox(Text = "Right mouse button exits fly mode")
    let exitOnMouseMiddle = new CheckBox(Text = "Middle mouse button exits fly mode")

    let middleMouseTogglesPivot =
        new CheckBox(Text = "Middle mouse toggles pivot while flying")

    let mouse4TogglesPivotWhileFlying =
        new CheckBox(Text = "Mouse 4 toggles pivot while flying")

    let mouse5TogglesPivotWhileFlying =
        new CheckBox(Text = "Mouse 5 toggles pivot while flying")

    let hijackRightClick = new CheckBox(Text = "Hijack right click")

    let hijackRightClickDuringCommands =
        new CheckBox(Text = "Hijack right click during commands")

    let commandsDoNotRepeat =
        new CheckBox(Text = "Don't count fly command as repeatable")

    let boostHold = new CheckBox(Text = "Boost Mode: hold instead of toggle")
    let slowHold = new CheckBox(Text = "Slow Mode: hold instead of toggle")

    let mouseButtonOverridesEnabled =
        new CheckBox(Text = "Enable mouse button overrides")

    let mouse4AsMiddle = new CheckBox(Text = "Mouse 4 held pivots")
    let mouse5AsMiddle = new CheckBox(Text = "Mouse 5 held pivots")
    let mouse4TogglesMiddle = new CheckBox(Text = "Mouse 4 toggle pivots")
    let mouse5TogglesMiddle = new CheckBox(Text = "Mouse 5 toggle pivots")

    let shiftRightClickTogglesView = new CheckBox(Text = "Shift + right click pivots")

    let altRightClickTogglesView = new CheckBox(Text = "Alt + right click pivots")
    let shiftRightClickPans = new CheckBox(Text = "Shift + right click pans")
    let altRightClickPans = new CheckBox(Text = "Alt + right click pans")

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

    let format_runtime_number (value: float) =
        value.ToString("0.######", CultureInfo.InvariantCulture)

    let is_checked (control: CheckBox) = control.Checked.GetValueOrDefault()

    let set_checked (control: CheckBox) (value: bool) = control.Checked <- Nullable value

    let redraw_mode_index (value: string) =
        match ConfigSchema.parse_viewport_redraw_mode value with
        | Ok mode ->
            redrawModes
            |> Array.tryFindIndex (fun (candidate: ViewportRedrawMode, _: string) -> candidate = mode)
            |> Option.defaultValue 0
        | Error _ -> 0

    let selected_redraw_mode () =
        let index = viewportRedrawMode.SelectedIndex

        let mode =
            if index >= 0 && index < redrawModes.Length then
                fst redrawModes[index]
            else
                ViewportRedrawMode.Rhino

        ConfigSchema.viewport_redraw_mode_value mode

    let refresh_status () =
        statusLine.Text <-
            match configurationError with
            | Some error -> $"Version: {versionText}  |  Configuration error: {error}"
            | None -> $"Version: {versionText}"

        runtimeLine.Text <- $"Current Speed: {speedText}  |  Current Lens: {lensText}"

    let refresh_mouse_override_controls () =
        let enabled = is_checked mouseButtonOverridesEnabled
        mouse4AsMiddle.Enabled <- enabled
        mouse5AsMiddle.Enabled <- enabled
        mouse4TogglesMiddle.Enabled <- enabled
        mouse5TogglesMiddle.Enabled <- enabled
        shiftRightClickTogglesView.Enabled <- enabled
        altRightClickTogglesView.Enabled <- enabled
        shiftRightClickPans.Enabled <- enabled
        altRightClickPans.Enabled <- enabled

    let refresh_right_click_controls () =
        hijackRightClickDuringCommands.Enabled <- is_checked hijackRightClick

    let parse_number (name: string) (field: TextBox) =
        let mutable value = 0.

        if
            Double.TryParse(field.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, &value)
            || Double.TryParse(field.Text.Trim(), NumberStyles.Float, CultureInfo.CurrentCulture, &value)
        then
            Ok value
        else
            Error $"{name} must be a number."

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

    let mainTable = new TableLayout(Padding = Padding 12, Spacing = Size(0, 4))

    do
        mainTable.Rows.Add(SettingsLayout.full_width (title_row ()))
        mainTable.Rows.Add(SettingsLayout.full_width (SettingsLayout.heading "General behavior"))

        SettingsLayout.grid
            3
            [ pluginEnabled
              normalizeDiagonal
              commandsDoNotRepeat
              boostHold
              slowHold
              saveSpeedToDocument
              loadSpeedFromDocument
              hideGumball
              pivotBindingsIgnoreGumball ]
        |> SettingsLayout.full_width
        |> mainTable.Rows.Add

        mainTable.Rows.Add(SettingsLayout.full_width (SettingsLayout.heading "Right click behavior"))

        SettingsLayout.grid 3 [ hijackRightClick; hijackRightClickDuringCommands; exitOnMouseRight ]
        |> SettingsLayout.full_width
        |> mainTable.Rows.Add

        mainTable.Rows.Add(SettingsLayout.full_width (SettingsLayout.heading "Mouse behavior"))

        SettingsLayout.grid
            3
            [ wheelChangesSpeed
              invertMouseX
              invertMouseY
              exitOnMouseLeft
              exitOnMouseMiddle
              middleMouseTogglesPivot ]
        |> SettingsLayout.full_width
        |> mainTable.Rows.Add

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
              SettingsLayout.item "Boost Mode" (binding_editor boost defaults.boost_toggle)
              SettingsLayout.item "Slow Mode" (binding_editor slow defaults.slow)
              SettingsLayout.item "Increase speed" (binding_editor speedIncrease defaults.speed_increase)
              SettingsLayout.item "Decrease speed" (binding_editor speedDecrease defaults.speed_decrease)
              SettingsLayout.item "Exit fly mode" (binding_editor exitKey defaults.exit_key) ]
        |> SettingsLayout.full_width
        |> mainTable.Rows.Add

        mainTable.Rows.Add(SettingsLayout.full_width (SettingsLayout.heading "Speed and mouse"))

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
              SettingsLayout.item "Fly + pivot sens multi" mousePivotMultiplier
              SettingsLayout.item "Mouse sensitivity" mouseSensitivity
              SettingsLayout.item "Force lens length" lensLength ]
        |> SettingsLayout.full_width
        |> mainTable.Rows.Add

        mainTable.Rows.Add(SettingsLayout.full_width (SettingsLayout.heading "Viewport redraw"))
        mainTable.Rows.Add(SettingsLayout.full_width (SettingsLayout.item "Mode" viewportRedrawMode))

        mainTable.Rows.Add(
            SettingsLayout.full_width (SettingsLayout.note "Try an alternative mode only if fly mode is not smooth.")
        )

        mainTable.Rows.Add(
            SettingsLayout.full_width (
                SettingsLayout.heading
                    "Mouse override behavior (Uses middle mouse button pan/rotate settings from Rhino itself)"
            )
        )

        SettingsLayout.grid
            3
            [ mouseButtonOverridesEnabled
              shiftRightClickTogglesView
              shiftRightClickPans
              altRightClickTogglesView
              altRightClickPans ]
        |> SettingsLayout.full_width
        |> mainTable.Rows.Add

        mainTable.Rows.Add(
            SettingsLayout.full_width (
                SettingsLayout.note "Right click pivots stop with right click. Alt pan starts after Alt is released."
            )
        )

        mainTable.Rows.Add(SettingsLayout.full_width (SettingsLayout.heading "Mouse 4/5 override behavior"))

        SettingsLayout.grid
            3
            [ mouse4TogglesMiddle
              mouse4TogglesPivotWhileFlying
              mouse4AsMiddle
              mouse5TogglesMiddle
              mouse5TogglesPivotWhileFlying
              mouse5AsMiddle ]
        |> SettingsLayout.full_width
        |> mainTable.Rows.Add

        mainTable.Rows.Add(
            SettingsLayout.full_width (SettingsLayout.note "Toggle pivots stop with the same side button.")
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

        mouseButtonOverridesEnabled.CheckedChanged.Add(fun (_: EventArgs) -> refresh_mouse_override_controls ())
        hijackRightClick.CheckedChanged.Add(fun (_: EventArgs) -> refresh_right_click_controls ())

        let make_exclusive (left: CheckBox) (right: CheckBox) =
            left.CheckedChanged.Add(fun (_: EventArgs) ->
                if is_checked left then
                    set_checked right false)

            right.CheckedChanged.Add(fun (_: EventArgs) ->
                if is_checked right then
                    set_checked left false)

        make_exclusive mouse4AsMiddle mouse4TogglesMiddle
        make_exclusive mouse5AsMiddle mouse5TogglesMiddle
        make_exclusive exitOnMouseMiddle middleMouseTogglesPivot

        refresh_mouse_override_controls ()
        refresh_right_click_controls ()

        rawJsonToggle.Click.Add(fun (_: EventArgs) ->
            rawJsonPanel.Visible <- not rawJsonPanel.Visible

            rawJsonToggle.Text <-
                if rawJsonPanel.Visible then
                    "Hide raw JSON configuration"
                else
                    "Show raw JSON configuration")

        github.Click.Add(fun (_: EventArgs) ->
            Process.Start(ProcessStartInfo("https://github.com/Viterkim/RhinosCanFly", UseShellExecute = true))
            |> ignore)

        resetAll.Click.Add(fun (_: EventArgs) ->
            self.LoadConfig defaults

            match RuntimeSettings.save_apply_and_set_speed RhinoDoc.ActiveDoc defaults defaults.base_speed with
            | Ok _ ->
                speedText <- ConfigSchema.format_number defaults.base_speed
                refresh_status ()

                match ConfigStorage.read_raw () with
                | Ok(path, content) -> self.ShowRaw(path, content)
                | Error _ -> ()

                self.ClearError()
            | Error error -> self.ShowError error)

        BindingCapture.attach_mouse_behavior bindingCapture self
        SettingsUi.use_rhino_style self
        self.UnLoad.Add(fun (_: EventArgs) -> BindingCapture.stop bindingCapture)

    override _.Dispose(disposing: bool) =
        if disposing then
            BindingCapture.dispose bindingCapture

        base.Dispose disposing

        if disposing then
            optionsIcon |> Option.iter (fun (icon: Icon) -> icon.Dispose())

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

    member _.ShowRaw(path: string, content: string) =
        configPath.Text <- path
        rawJson.Text <- content

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
        boost.Text <- config.boost_toggle
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
        lensLength.Text <- ConfigSchema.format_number config.lens_length_mm_in_mode
        viewportRedrawMode.SelectedIndex <- redraw_mode_index config.viewport_redraw_mode
        set_checked invertMouseX config.invert_mouse_x
        set_checked invertMouseY config.invert_mouse_y
        set_checked normalizeDiagonal config.normalize_diagonal_movement
        set_checked hideGumball config.hide_gumball_while_flying
        set_checked pivotBindingsIgnoreGumball config.pivot_bindings_ignore_gumball
        set_checked saveSpeedToDocument config.save_speed_to_document
        set_checked loadSpeedFromDocument config.load_speed_from_document
        set_checked wheelChangesSpeed config.wheel_changes_speed
        set_checked exitOnMouseLeft config.exit_on_mouse_left
        set_checked exitOnMouseRight config.exit_on_mouse_right
        set_checked exitOnMouseMiddle config.exit_on_mouse_middle
        set_checked middleMouseTogglesPivot config.middle_mouse_toggles_pivot_while_flying
        set_checked mouse4TogglesPivotWhileFlying config.mouse4_toggles_pivot_while_flying
        set_checked mouse5TogglesPivotWhileFlying config.mouse5_toggles_pivot_while_flying
        set_checked hijackRightClick config.hijack_right_click_to_enter
        set_checked hijackRightClickDuringCommands config.hijack_right_click_during_commands
        refresh_right_click_controls ()
        set_checked commandsDoNotRepeat config.commands_do_not_repeat
        set_checked mouseButtonOverridesEnabled config.mouse_button_overrides_enabled
        set_checked mouse4AsMiddle config.mouse4_acts_as_middle
        set_checked mouse5AsMiddle config.mouse5_acts_as_middle
        set_checked mouse4TogglesMiddle config.mouse4_toggles_middle
        set_checked mouse5TogglesMiddle config.mouse5_toggles_middle
        set_checked shiftRightClickTogglesView config.shift_right_click_toggles_view
        set_checked altRightClickTogglesView config.alt_right_click_toggles_view
        set_checked shiftRightClickPans config.shift_right_click_pans
        set_checked altRightClickPans config.alt_right_click_pans
        refresh_mouse_override_controls ()
        set_checked boostHold config.boost_hold_instead_of_toggle
        set_checked slowHold config.slow_hold_instead_of_toggle

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
            parse_number "Lens length" lensLength
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
          Ok lensValue ->
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
                  boost_toggle = boost.Text
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
                  invert_mouse_x = is_checked invertMouseX
                  invert_mouse_y = is_checked invertMouseY
                  normalize_diagonal_movement = is_checked normalizeDiagonal
                  hide_gumball_while_flying = is_checked hideGumball
                  pivot_bindings_ignore_gumball = is_checked pivotBindingsIgnoreGumball
                  save_speed_to_document = is_checked saveSpeedToDocument
                  load_speed_from_document = is_checked loadSpeedFromDocument
                  wheel_changes_speed = is_checked wheelChangesSpeed
                  exit_on_mouse_left = is_checked exitOnMouseLeft
                  exit_on_mouse_right = is_checked exitOnMouseRight
                  exit_on_mouse_middle = is_checked exitOnMouseMiddle
                  middle_mouse_toggles_pivot_while_flying = is_checked middleMouseTogglesPivot
                  mouse4_toggles_pivot_while_flying = is_checked mouse4TogglesPivotWhileFlying
                  mouse5_toggles_pivot_while_flying = is_checked mouse5TogglesPivotWhileFlying
                  hijack_right_click_to_enter = is_checked hijackRightClick
                  hijack_right_click_during_commands = is_checked hijackRightClickDuringCommands
                  commands_do_not_repeat = is_checked commandsDoNotRepeat
                  mouse_button_overrides_enabled = is_checked mouseButtonOverridesEnabled
                  mouse4_acts_as_middle = is_checked mouse4AsMiddle
                  mouse5_acts_as_middle = is_checked mouse5AsMiddle
                  mouse4_toggles_middle = is_checked mouse4TogglesMiddle
                  mouse5_toggles_middle = is_checked mouse5TogglesMiddle
                  shift_right_click_toggles_view = is_checked shiftRightClickTogglesView
                  alt_right_click_toggles_view = is_checked altRightClickTogglesView
                  shift_right_click_pans = is_checked shiftRightClickPans
                  alt_right_click_pans = is_checked altRightClickPans
                  boost_hold_instead_of_toggle = is_checked boostHold
                  slow_hold_instead_of_toggle = is_checked slowHold
                  vertical_speed_multiplier = verticalValue
                  lens_length_mm_in_mode = lensValue
                  viewport_redraw_mode = selected_redraw_mode () }
        | a, b, c, d, e, f, g, h, i, j, k ->
            [ a; b; c; d; e; f; g; h; i; j; k ]
            |> List.choose (function
                | Error error -> Some error
                | Ok _ -> None)
            |> String.concat Environment.NewLine
            |> Error
