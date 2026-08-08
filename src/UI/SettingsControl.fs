namespace RhinosCanFly

open System
open System.Diagnostics
open System.Globalization
open System.Reflection
open Eto.Drawing
open Eto.Forms
open Rhino
open Rhino.UI

module SettingsUi =
    let load_icon () =
        let assembly = Assembly.GetExecutingAssembly()

        let stream =
            assembly.GetManifestResourceStream "RhinosCanFly.Resources.PluginIcon.ico"

        if isNull stream then
            None
        else
            use source = stream
            Some(new Icon(source))

    let use_rhino_style (control: Control) =
        let method =
            typeof<EtoExtensions>
                .GetMethod(
                    "UseRhinoStyle",
                    BindingFlags.Public ||| BindingFlags.Static,
                    null,
                    [| typeof<Control> |],
                    null
                )

        if not (isNull method) then
            method.Invoke(null, [| control :> obj |]) |> ignore

type SettingsControl() as self =
    inherit Panel()

    let text_box () = new TextBox(Height = 24)

    let defaults = Config.default_config ()

    let forward = text_box ()
    let backward = text_box ()
    let left = text_box ()
    let right = text_box ()
    let up = text_box ()
    let down = text_box ()
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
    let mouseSensitivity = text_box ()
    let lensLength = text_box ()

    let viewportRedrawMode =
        let control = new DropDown(Height = 24)
        control.Items.Add "Rhino redraw (default)"
        control.Items.Add "Rhino redraw with immediate paint"
        control.Items.Add "Native window redraw (experimental)"
        control

    let invertMouseX = new CheckBox(Text = "Invert mouse X")
    let invertMouseY = new CheckBox(Text = "Invert mouse Y")
    let normalizeDiagonal = new CheckBox(Text = "Normalize diagonal movement")
    let hideGumball = new CheckBox(Text = "Hide gumball while flying")
    let saveSpeedToDocument = new CheckBox(Text = "Save current speed to file")
    let loadSpeedFromDocument = new CheckBox(Text = "Load speed from file")
    let wheelChangesSpeed = new CheckBox(Text = "Mouse wheel up/down controls speed")
    let exitOnMouseLeft = new CheckBox(Text = "Left mouse button exits fly mode")
    let exitOnMouseRight = new CheckBox(Text = "Right mouse button exits fly mode")
    let exitOnMouseMiddle = new CheckBox(Text = "Middle mouse button exits fly mode")
    let hijackRightClick = new CheckBox(Text = "Hijack right click")

    let hijackRightClickDuringCommands =
        new CheckBox(Text = "Hijack right click during commands")

    let commandsDoNotRepeat =
        new CheckBox(Text = "Don't count fly command as repeatable")

    let boostHold = new CheckBox(Text = "Boost Mode: hold instead of toggle")
    let slowHold = new CheckBox(Text = "Slow Mode: hold instead of toggle")

    let mouseButtonOverridesEnabled =
        new CheckBox(Text = "Enable mouse button overrides (experimental)")

    let mouse4AsMiddle = new CheckBox(Text = "Mouse 4 drag manipulates view")
    let mouse5AsMiddle = new CheckBox(Text = "Mouse 5 drag manipulates view")
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

    let format (value: float) =
        let rounded = Math.Round(value, 9, MidpointRounding.AwayFromZero)
        rounded.ToString("G15", CultureInfo.InvariantCulture)

    let is_checked (control: CheckBox) = control.Checked.GetValueOrDefault()

    let set_checked (control: CheckBox) (value: bool) = control.Checked <- Nullable value

    let redraw_mode_index (value: string) =
        match Config.parse_viewport_redraw_mode value with
        | Ok ViewportRedrawMode.Rhino -> 0
        | Ok ViewportRedrawMode.RhinoImmediate -> 1
        | Ok ViewportRedrawMode.NativeWindow -> 2
        | Error _ -> 0

    let selected_redraw_mode () =
        match viewportRedrawMode.SelectedIndex with
        | 1 -> Config.viewport_redraw_mode_value ViewportRedrawMode.RhinoImmediate
        | 2 -> Config.viewport_redraw_mode_value ViewportRedrawMode.NativeWindow
        | _ -> Config.viewport_redraw_mode_value ViewportRedrawMode.Rhino

    let make_row (cells: TableCell list) =
        let row = new TableRow()

        for cell in cells do
            row.Cells.Add cell

        row

    let full_width_row (control: Control) =
        make_row [ new TableCell(control, true) ]

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

    let grid_item (label: string) (control: Control) =
        let caption = new Label(Text = label, Width = 140)
        let item = new TableLayout(Spacing = Size(8, 0))

        item.Rows.Add(make_row [ new TableCell(caption, false); new TableCell(control, true) ])

        item

    let two_column_grid (controls: Control list) =
        let grid = new TableLayout(Spacing = Size(16, 4))

        let rec add_rows (remaining: Control list) =
            match remaining with
            | first :: second :: rest ->
                grid.Rows.Add(make_row [ new TableCell(first, true); new TableCell(second, true) ])

                add_rows rest
            | [ first ] -> grid.Rows.Add(make_row [ new TableCell(first, true); new TableCell(new Panel(), true) ])
            | [] -> ()

        add_rows controls
        grid

    let three_column_grid (controls: Control list) =
        let grid = new TableLayout(Spacing = Size(16, 4))

        let rec add_rows (remaining: Control list) =
            match remaining with
            | first :: second :: third :: rest ->
                grid.Rows.Add(
                    make_row
                        [ new TableCell(first, true)
                          new TableCell(second, true)
                          new TableCell(third, true) ]
                )

                add_rows rest
            | [ first; second ] ->
                grid.Rows.Add(
                    make_row
                        [ new TableCell(first, true)
                          new TableCell(second, true)
                          new TableCell(new Panel(), true) ]
                )
            | [ first ] ->
                grid.Rows.Add(
                    make_row
                        [ new TableCell(first, true)
                          new TableCell(new Panel(), true)
                          new TableCell(new Panel(), true) ]
                )
            | [] -> ()

        add_rows controls
        grid

    let binding_editor (field: TextBox) (defaultValue: string) =
        BindingCapture.editor bindingCapture field defaultValue

    let heading (text: string) =
        let label =
            new Label(Text = text, Font = SystemFonts.Bold(Nullable(), FontDecoration.None))

        new Panel(Content = label, Padding = Padding(0, 10, 0, 2)) :> Control

    let note (text: string) =
        new Label(Text = text, Wrap = WrapMode.Word) :> Control

    let title_row () =
        let image = new ImageView(Size = Size(32, 32))

        optionsIcon |> Option.iter (fun (icon: Icon) -> image.Image <- icon)

        let title =
            new Label(Text = "Rhinos Can Fly Options", Font = SystemFonts.Bold(Nullable 15.f, FontDecoration.None))

        let row = new TableLayout(Spacing = Size(10, 0))

        row.Rows.Add(make_row [ new TableCell(image, false); new TableCell(title, true) ])

        row :> Control

    let rawJsonLayout =
        let layout = new TableLayout(Spacing = Size(8, 6))
        let pathLabel = new Label(Text = "File", Width = 64)
        let contentsLabel = new Label(Text = "Contents", Width = 64)

        layout.Rows.Add(make_row [ new TableCell(pathLabel, false); new TableCell(configPath, true) ])

        layout.Rows.Add(make_row [ new TableCell(contentsLabel, false); new TableCell(rawJson, true) ])

        layout

    let rawJsonPanel = new Panel(Content = rawJsonLayout, Visible = false)

    let mainTable = new TableLayout(Padding = Padding 12, Spacing = Size(0, 4))

    do
        mainTable.Rows.Add(full_width_row (title_row ()))
        mainTable.Rows.Add(full_width_row (heading "General behavior"))

        three_column_grid
            [ normalizeDiagonal
              boostHold
              slowHold
              commandsDoNotRepeat
              saveSpeedToDocument
              loadSpeedFromDocument
              hideGumball ]
        |> full_width_row
        |> mainTable.Rows.Add

        mainTable.Rows.Add(full_width_row (heading "Right click behavior"))

        three_column_grid [ hijackRightClick; hijackRightClickDuringCommands; exitOnMouseRight ]
        |> full_width_row
        |> mainTable.Rows.Add

        mainTable.Rows.Add(full_width_row (heading "Mouse behavior"))

        three_column_grid
            [ invertMouseX
              invertMouseY
              wheelChangesSpeed
              exitOnMouseLeft
              exitOnMouseMiddle ]
        |> full_width_row
        |> mainTable.Rows.Add

        mainTable.Rows.Add(full_width_row (heading "Controls"))

        two_column_grid
            [ grid_item "Forward" (binding_editor forward defaults.forward)
              grid_item "Backward" (binding_editor backward defaults.backward)
              grid_item "Move left" (binding_editor left defaults.left)
              grid_item "Move right" (binding_editor right defaults.right)
              grid_item "Move up" (binding_editor up defaults.up)
              grid_item "Move down" (binding_editor down defaults.down)
              grid_item "Boost Mode" (binding_editor boost defaults.boost_toggle)
              grid_item "Slow Mode" (binding_editor slow defaults.slow)
              grid_item "Increase speed" (binding_editor speedIncrease defaults.speed_increase)
              grid_item "Decrease speed" (binding_editor speedDecrease defaults.speed_decrease)
              grid_item "Exit fly mode" (binding_editor exitKey defaults.exit_key) ]
        |> full_width_row
        |> mainTable.Rows.Add

        mainTable.Rows.Add(full_width_row (heading "Speed and mouse"))

        two_column_grid
            [ grid_item "Base speed" baseSpeed
              grid_item "Minimum speed" minimumSpeed
              grid_item "Maximum speed" maximumSpeed
              grid_item "Speed step multiplier" speedStep
              grid_item "Boost multiplier" boostMultiplier
              grid_item "Slow multiplier" slowMultiplier
              grid_item "Move up/down multiplier" verticalSpeedMultiplier
              grid_item "Mouse sensitivity" mouseSensitivity
              grid_item "Force lens length" lensLength ]
        |> full_width_row
        |> mainTable.Rows.Add

        mainTable.Rows.Add(full_width_row (heading "Viewport redraw"))
        mainTable.Rows.Add(full_width_row (grid_item "Mode" viewportRedrawMode))
        mainTable.Rows.Add(full_width_row (note "Try an alternative mode only if fly mode is not smooth."))

        mainTable.Rows.Add(full_width_row (heading "Override mouse button behavior"))

        three_column_grid [ mouseButtonOverridesEnabled; mouse4AsMiddle; mouse5AsMiddle ]
        |> full_width_row
        |> mainTable.Rows.Add

        mainTable.Rows.Add(
            full_width_row (note "Uses Rhino's middle-button Pan/Rotate/Swap settings. Side-button clicks are ignored.")
        )

        mainTable.Rows.Add(full_width_row (heading "Status"))
        refresh_status ()
        mainTable.Rows.Add(full_width_row statusLine)
        mainTable.Rows.Add(full_width_row runtimeLine)
        mainTable.Rows.Add(full_width_row github)
        mainTable.Rows.Add(full_width_row resetAll)
        mainTable.Rows.Add(full_width_row rawJsonToggle)
        mainTable.Rows.Add(full_width_row rawJsonPanel)

        let scrollable =
            new Scrollable(Border = BorderType.None, ExpandContentWidth = true, Content = mainTable)

        let host = new TableLayout(Spacing = Size.Empty)
        host.Rows.Add(make_row [ new TableCell(bindingCapture.focus_sink, false) ])

        let contentRow = make_row [ new TableCell(scrollable, true) ]
        contentRow.ScaleHeight <- true
        host.Rows.Add contentRow

        self.Content <- host

        mouseButtonOverridesEnabled.CheckedChanged.Add(fun (_: EventArgs) -> refresh_mouse_override_controls ())
        hijackRightClick.CheckedChanged.Add(fun (_: EventArgs) -> refresh_right_click_controls ())

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

            match RuntimeSettings.save_and_apply RhinoDoc.ActiveDoc defaults defaults.base_speed with
            | Ok _ ->
                speedText <- format defaults.base_speed
                refresh_status ()

                match Config.read_raw () with
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
        speedText <- format speed

        lensText <-
            match lens with
            | Some value -> $"{format value} mm"
            | None -> "Unavailable"

        refresh_status ()

    member _.ShowRaw(path: string, content: string) =
        configPath.Text <- path
        rawJson.Text <- content

    member _.LoadConfig(config: FlyConfigFile) =
        forward.Text <- config.forward
        backward.Text <- config.backward
        left.Text <- config.left
        right.Text <- config.right
        up.Text <- config.up
        down.Text <- config.down
        boost.Text <- config.boost_toggle
        slow.Text <- config.slow
        speedIncrease.Text <- config.speed_increase
        speedDecrease.Text <- config.speed_decrease
        exitKey.Text <- config.exit_key
        baseSpeed.Text <- format config.base_speed
        minimumSpeed.Text <- format config.minimum_speed
        maximumSpeed.Text <- format config.maximum_speed
        speedStep.Text <- format config.speed_step_multiplier
        boostMultiplier.Text <- format config.boost_multiplier
        slowMultiplier.Text <- format config.slow_multiplier
        verticalSpeedMultiplier.Text <- format config.vertical_speed_multiplier
        mouseSensitivity.Text <- format config.mouse_sensitivity
        lensLength.Text <- format config.lens_length_mm_in_mode
        viewportRedrawMode.SelectedIndex <- redraw_mode_index config.viewport_redraw_mode
        set_checked invertMouseX config.invert_mouse_x
        set_checked invertMouseY config.invert_mouse_y
        set_checked normalizeDiagonal config.normalize_diagonal_movement
        set_checked hideGumball config.hide_gumball_while_flying
        set_checked saveSpeedToDocument config.save_speed_to_document
        set_checked loadSpeedFromDocument config.load_speed_from_document
        set_checked wheelChangesSpeed config.wheel_changes_speed
        set_checked exitOnMouseLeft config.exit_on_mouse_left
        set_checked exitOnMouseRight config.exit_on_mouse_right
        set_checked exitOnMouseMiddle config.exit_on_mouse_middle
        set_checked hijackRightClick config.hijack_right_click_to_enter
        set_checked hijackRightClickDuringCommands config.hijack_right_click_during_commands
        refresh_right_click_controls ()
        set_checked commandsDoNotRepeat config.commands_do_not_repeat
        set_checked mouseButtonOverridesEnabled config.mouse_button_overrides_enabled
        set_checked mouse4AsMiddle config.mouse4_acts_as_middle
        set_checked mouse5AsMiddle config.mouse5_acts_as_middle
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
          Ok sensitivityValue,
          Ok lensValue ->
            Ok
                { config_version = Config.CurrentVersion
                  forward = forward.Text
                  backward = backward.Text
                  left = left.Text
                  right = right.Text
                  up = up.Text
                  down = down.Text
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
                  mouse_sensitivity = sensitivityValue
                  invert_mouse_x = is_checked invertMouseX
                  invert_mouse_y = is_checked invertMouseY
                  normalize_diagonal_movement = is_checked normalizeDiagonal
                  hide_gumball_while_flying = is_checked hideGumball
                  save_speed_to_document = is_checked saveSpeedToDocument
                  load_speed_from_document = is_checked loadSpeedFromDocument
                  wheel_changes_speed = is_checked wheelChangesSpeed
                  exit_on_mouse_left = is_checked exitOnMouseLeft
                  exit_on_mouse_right = is_checked exitOnMouseRight
                  exit_on_mouse_middle = is_checked exitOnMouseMiddle
                  hijack_right_click_to_enter = is_checked hijackRightClick
                  hijack_right_click_during_commands = is_checked hijackRightClickDuringCommands
                  commands_do_not_repeat = is_checked commandsDoNotRepeat
                  mouse_button_overrides_enabled = is_checked mouseButtonOverridesEnabled
                  mouse4_acts_as_middle = is_checked mouse4AsMiddle
                  mouse5_acts_as_middle = is_checked mouse5AsMiddle
                  boost_hold_instead_of_toggle = is_checked boostHold
                  slow_hold_instead_of_toggle = is_checked slowHold
                  vertical_speed_multiplier = verticalValue
                  lens_length_mm_in_mode = lensValue
                  viewport_redraw_mode = selected_redraw_mode () }
        | a, b, c, d, e, f, g, h, i ->
            [ a; b; c; d; e; f; g; h; i ]
            |> List.choose (function
                | Error error -> Some error
                | Ok _ -> None)
            |> String.concat Environment.NewLine
            |> Error
