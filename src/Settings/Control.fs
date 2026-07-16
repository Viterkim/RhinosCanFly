namespace RhinosCanFly

open System
open System.Diagnostics
open System.Drawing
open System.Globalization
open System.Reflection
open System.Windows.Forms
open Rhino
open Rhino.ApplicationSettings
open Rhino.UI

type SettingsControl() as self =
    inherit UserControl()

    let defaults = Config.default_config ()

    let forward = new TextBox()
    let backward = new TextBox()
    let left = new TextBox()
    let right = new TextBox()
    let up = new TextBox()
    let down = new TextBox()
    let boost = new TextBox()
    let slow = new TextBox()
    let speedIncrease = new TextBox()
    let speedDecrease = new TextBox()
    let exitKey = new TextBox()
    let baseSpeed = new TextBox()
    let minimumSpeed = new TextBox()
    let maximumSpeed = new TextBox()
    let speedStep = new TextBox()
    let boostMultiplier = new TextBox()
    let slowMultiplier = new TextBox()
    let verticalSpeedMultiplier = new TextBox()
    let mouseSensitivity = new TextBox()
    let updateHz = new TextBox()
    let lensLength = new TextBox()
    let invertMouseX = new CheckBox(Text = "Invert mouse X")
    let invertMouseY = new CheckBox(Text = "Invert mouse Y")

    let normalizeDiagonal = new CheckBox(Text = "Normalize diagonal movement")

    let wheelChangesSpeed = new CheckBox(Text = "Mouse wheel up/down controls speed")

    let exitOnMouseLeft = new CheckBox(Text = "Left mouse button exits fly mode")
    let exitOnMouseRight = new CheckBox(Text = "Right mouse button exits fly mode")
    let exitOnMouseMiddle = new CheckBox(Text = "Middle mouse button exits fly mode")

    let hijackRightClick = new CheckBox(Text = "Hijack right click")

    let commandsDoNotRepeat =
        new CheckBox(Text = "Don't count fly/init commands as repeatable")

    let boostHold = new CheckBox(Text = "Boost Mode: hold instead of toggle")
    let slowHold = new CheckBox(Text = "Slow Mode: hold instead of toggle")
    let statusLine = new Label(AutoSize = true, MaximumSize = Size(900, 0))
    let runtimeLine = new Label(AutoSize = true, MaximumSize = Size(900, 0))
    let configPath = new TextBox(ReadOnly = true)

    let rawJson =
        new TextBox(ReadOnly = true, Multiline = true, WordWrap = false, ScrollBars = ScrollBars.Both, Height = 120)

    let resetAll = new Button(Text = "Reset all to defaults", AutoSize = true)
    let github = new LinkLabel(Text = "Viterkim/RhinosCanFly", AutoSize = true)
    let version = Assembly.GetExecutingAssembly().GetName().Version
    let versionText = $"{version.Major}.{version.Minor}.{version.Build}"
    let mutable configurationText = "Not loaded"
    let mutable speedText = "Unavailable"
    let mutable lensText = "Unavailable"
    let mutable appliedTheme: (Color * Color * Color) option = None

    let format (value: float) =
        let rounded = Math.Round(value, 9, MidpointRounding.AwayFromZero)
        rounded.ToString("G15", CultureInfo.InvariantCulture)

    let refresh_status () =
        statusLine.Text <- $"Version: {versionText}  |  Configuration: {configurationText}"
        runtimeLine.Text <- $"Current Speed: {speedText}  |  Current Lens: {lensText}"

    let parse_number (name: string) (field: TextBox) =
        let mutable value = 0.
        // JSON-style decimal points come first, and thousands separators are
        // deliberately rejected. Under da-DK, "0.00001" otherwise becomes 1.
        if
            Double.TryParse(field.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, &value)
            || Double.TryParse(field.Text.Trim(), NumberStyles.Float, CultureInfo.CurrentCulture, &value)
        then
            Ok value
        else
            Error $"{name} must be a number."

    let table =
        let value =
            new TableLayoutPanel(Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, Padding = Padding 12)

        value.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize)) |> ignore
        value.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100.f)) |> ignore
        value

    let add_row (label: string) (control: Control) =
        let row = table.RowCount
        table.RowCount <- row + 1
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize)) |> ignore

        let caption =
            new Label(Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = Padding(3, 7, 12, 3))

        control.Dock <- DockStyle.Fill
        table.Controls.Add(caption, 0, row)
        table.Controls.Add(control, 1, row)

    let two_column_grid () =
        let grid =
            new TableLayoutPanel(Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, Margin = Padding 0)

        grid.SuspendLayout()
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50.f)) |> ignore
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50.f)) |> ignore
        grid

    let add_grid_item (grid: TableLayoutPanel) (label: string) (control: Control) =
        let index = grid.Controls.Count
        let column = index % 2
        let row = index / 2

        if column = 0 then
            grid.RowCount <- row + 1
            grid.RowStyles.Add(new RowStyle(SizeType.AutoSize)) |> ignore

        let item =
            new TableLayoutPanel(Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2)

        item.SuspendLayout()

        item.Margin <-
            if column = 0 then
                Padding(0, 0, 8, 0)
            else
                Padding(8, 0, 0, 0)

        item.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize)) |> ignore
        item.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100.f)) |> ignore

        let caption =
            new Label(Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = Padding(3, 7, 8, 3))

        control.Dock <- DockStyle.Fill
        item.Controls.Add(caption, 0, 0)
        item.Controls.Add(control, 1, 0)
        item.ResumeLayout(false)
        grid.Controls.Add(item, column, row)

    let add_grid_control (grid: TableLayoutPanel) (control: Control) =
        let index = grid.Controls.Count
        let column = index % 2
        let row = index / 2

        if column = 0 then
            grid.RowCount <- row + 1
            grid.RowStyles.Add(new RowStyle(SizeType.AutoSize)) |> ignore

        control.AutoSize <- true
        control.Dock <- DockStyle.Fill

        control.Margin <-
            if column = 0 then
                Padding(3, 5, 8, 3)
            else
                Padding(8, 5, 3, 3)

        grid.Controls.Add(control, column, row)

    let key_name (key: Keys) =
        match key with
        | Keys.LShiftKey -> "LeftShift"
        | Keys.RShiftKey -> "RightShift"
        | Keys.ShiftKey -> "Shift"
        | Keys.LMenu -> "LeftAlt"
        | Keys.RMenu -> "RightAlt"
        | Keys.Menu -> "Alt"
        | Keys.LControlKey -> "LeftControl"
        | Keys.RControlKey -> "RightControl"
        | Keys.ControlKey -> "Control"
        | Keys.Escape -> "Escape"
        | Keys.OemMinus -> "Minus"
        | Keys.Oemplus -> "Equals"
        | Keys.Oemcomma -> "Comma"
        | Keys.OemPeriod -> "Period"
        | Keys.OemQuestion -> "Slash"
        | Keys.OemSemicolon -> "Semicolon"
        | Keys.OemQuotes -> "Quote"
        | Keys.OemOpenBrackets -> "LeftBracket"
        | Keys.OemCloseBrackets -> "RightBracket"
        | Keys.OemPipe -> "Backslash"
        | Keys.Oemtilde -> "Backtick"
        | other -> other.ToString()

    let mouse_name (button: MouseButtons) =
        match button with
        | MouseButtons.Left -> Some "MouseLeft"
        | MouseButtons.Right -> Some "MouseRight"
        | MouseButtons.Middle -> Some "MouseMiddle"
        | MouseButtons.XButton1 -> Some "MouseX1"
        | MouseButtons.XButton2 -> Some "MouseX2"
        | _ -> None

    let modifier_names (modifiers: Keys) =
        [ if modifiers &&& Keys.Control = Keys.Control then
              "Control"
          if modifiers &&& Keys.Alt = Keys.Alt then
              "Alt"
          if modifiers &&& Keys.Shift = Keys.Shift then
              "Shift" ]

    let is_modifier_key (key: Keys) =
        match key with
        | Keys.ShiftKey
        | Keys.LShiftKey
        | Keys.RShiftKey
        | Keys.ControlKey
        | Keys.LControlKey
        | Keys.RControlKey
        | Keys.Menu
        | Keys.LMenu
        | Keys.RMenu -> true
        | _ -> false

    let chord_name (modifiers: Keys) (key: string) =
        String.concat "+" (modifier_names modifiers @ [ key ])

    let binding_editor (field: TextBox) (default_value: string) =
        let panel =
            new TableLayoutPanel(Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 3)

        panel.SuspendLayout()

        let small_button (text: string) =
            new Button(Text = text, AutoSize = false, Size = Size(62, 24), Anchor = AnchorStyles.Left)

        let setButton = small_button "Set..."
        let defaultButton = small_button "Default"
        let mutable capturing = false
        field.Dock <- DockStyle.Fill
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100.f)) |> ignore
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize)) |> ignore
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize)) |> ignore

        panel.Controls.Add(field, 0, 0)
        panel.Controls.Add(setButton, 1, 0)
        panel.Controls.Add(defaultButton, 2, 0)

        let stop () =
            capturing <- false
            setButton.Capture <- false
            setButton.Text <- "Set..."

        setButton.Click.Add(fun (_: EventArgs) ->
            capturing <- true
            setButton.Text <- "Press..."
            setButton.Capture <- true
            setButton.Focus() |> ignore)

        setButton.PreviewKeyDown.Add(fun (event: PreviewKeyDownEventArgs) ->
            if capturing then
                event.IsInputKey <- true)

        setButton.KeyDown.Add(fun (event: KeyEventArgs) ->
            if capturing then
                event.Handled <- true
                event.SuppressKeyPress <- true

                if not (is_modifier_key event.KeyCode) then
                    field.Text <- chord_name event.Modifiers (key_name event.KeyCode)
                    stop ())

        setButton.KeyUp.Add(fun (event: KeyEventArgs) ->
            if capturing && is_modifier_key event.KeyCode then
                event.Handled <- true
                event.SuppressKeyPress <- true
                field.Text <- key_name event.KeyCode
                stop ())

        setButton.MouseDown.Add(fun (event: MouseEventArgs) ->
            if capturing then
                match mouse_name event.Button with
                | Some binding ->
                    field.Text <- chord_name Control.ModifierKeys binding
                    stop ()
                | None -> ())

        setButton.LostFocus.Add(fun (_: EventArgs) ->
            if capturing then
                stop ())

        defaultButton.Click.Add(fun (_: EventArgs) -> field.Text <- default_value)
        panel.ResumeLayout(false)
        panel :> Control

    let add_heading (text: string) =
        let row = table.RowCount
        table.RowCount <- row + 1

        let heading =
            new Label(
                Text = text,
                AutoSize = true,
                Font = new Font(self.Font, FontStyle.Bold),
                Margin = Padding(3, 14, 3, 5)
            )

        table.Controls.Add(heading, 0, row)
        table.SetColumnSpan(heading, 2)

    let add_full_width (control: Control) =
        let row = table.RowCount
        table.RowCount <- row + 1
        control.Margin <- Padding(3, 3, 3, 6)
        table.Controls.Add(control, 0, row)
        table.SetColumnSpan(control, 2)

    let add_note (text: string) =
        let row = table.RowCount
        table.RowCount <- row + 1

        let note =
            new Label(Text = text, AutoSize = true, MaximumSize = Size(700, 0), Margin = Padding(3, 1, 3, 6))

        table.Controls.Add(note, 0, row)
        table.SetColumnSpan(note, 2)

    let rec apply_colors (control: Control) (panel: Color) (text: Color) (edit: Color) =
        control.ForeColor <- text

        match control with
        | :? TextBox as field -> field.BackColor <- edit
        | :? Button as button ->
            button.UseVisualStyleBackColor <- false
            button.BackColor <- panel
        | _ -> control.BackColor <- panel

        for child in control.Controls do
            apply_colors child panel text edit

    let dismiss_editor_focus () =
        self.ActiveControl <- null
        self.Focus() |> ignore

    let rec dismiss_on_background_click (control: Control) =
        match control with
        | :? TextBox
        | :? Button
        | :? CheckBox
        | :? LinkLabel -> ()
        | _ -> control.MouseDown.Add(fun (_: MouseEventArgs) -> dismiss_editor_focus ())

        for child in control.Controls do
            dismiss_on_background_click child

    do
        self.SuspendLayout()
        table.SuspendLayout()
        self.AutoScroll <- true
        self.SetStyle(ControlStyles.Selectable, true)

        let title =
            new Label(
                Text = "Rhinos Can Fly Options",
                AutoSize = true,
                Font = new Font(self.Font.FontFamily, self.Font.Size + 5.f, FontStyle.Bold)
            )

        add_full_width title
        add_heading "Behavior"
        let behaviorGrid = two_column_grid ()
        add_grid_control behaviorGrid exitOnMouseLeft
        add_grid_control behaviorGrid exitOnMouseRight
        add_grid_control behaviorGrid exitOnMouseMiddle
        add_grid_control behaviorGrid hijackRightClick
        add_grid_control behaviorGrid commandsDoNotRepeat
        add_grid_control behaviorGrid normalizeDiagonal
        add_grid_control behaviorGrid boostHold
        add_grid_control behaviorGrid slowHold
        add_grid_control behaviorGrid invertMouseX
        add_grid_control behaviorGrid invertMouseY
        add_grid_control behaviorGrid wheelChangesSpeed
        behaviorGrid.ResumeLayout(false)
        add_full_width behaviorGrid
        add_heading "Controls"

        let controlsGrid = two_column_grid ()
        add_grid_item controlsGrid "Forward" (binding_editor forward defaults.forward)
        add_grid_item controlsGrid "Backward" (binding_editor backward defaults.backward)
        add_grid_item controlsGrid "Move left" (binding_editor left defaults.left)
        add_grid_item controlsGrid "Move right" (binding_editor right defaults.right)
        add_grid_item controlsGrid "Move up" (binding_editor up defaults.up)
        add_grid_item controlsGrid "Move down" (binding_editor down defaults.down)
        add_grid_item controlsGrid "Boost Mode" (binding_editor boost defaults.boost_toggle)
        add_grid_item controlsGrid "Slow Mode" (binding_editor slow defaults.slow)
        add_grid_item controlsGrid "Increase speed" (binding_editor speedIncrease defaults.speed_increase)
        add_grid_item controlsGrid "Decrease speed" (binding_editor speedDecrease defaults.speed_decrease)
        add_grid_item controlsGrid "Exit fly mode" (binding_editor exitKey defaults.exit_key)
        controlsGrid.ResumeLayout(false)
        add_full_width controlsGrid

        add_heading "Speed and mouse"
        let speedGrid = two_column_grid ()
        add_grid_item speedGrid "Base speed" baseSpeed
        add_grid_item speedGrid "Minimum speed" minimumSpeed
        add_grid_item speedGrid "Maximum speed" maximumSpeed
        add_grid_item speedGrid "Speed step multiplier" speedStep
        add_grid_item speedGrid "Boost multiplier" boostMultiplier
        add_grid_item speedGrid "Slow multiplier" slowMultiplier
        add_grid_item speedGrid "Move up/down multiplier" verticalSpeedMultiplier
        add_grid_item speedGrid "Mouse sensitivity" mouseSensitivity
        add_grid_item speedGrid "Update rate (Hz)" updateHz
        add_grid_item speedGrid "Force lens length (mm)" lensLength
        speedGrid.ResumeLayout(false)
        add_full_width speedGrid

        add_heading "Raw JSON configuration"
        add_row "File" configPath
        add_row "Contents" rawJson
        add_heading "Status"
        refresh_status ()
        add_full_width statusLine
        add_full_width runtimeLine

        github.LinkClicked.Add(fun (_: LinkLabelLinkClickedEventArgs) ->
            Process.Start(ProcessStartInfo("https://github.com/Viterkim/RhinosCanFly", UseShellExecute = true))
            |> ignore)

        add_full_width github
        add_full_width resetAll

        resetAll.Click.Add(fun (_: EventArgs) ->
            self.LoadConfig defaults

            match Config.save defaults with
            | Ok() ->
                Runtime.reset_session_speed defaults.base_speed
                RightClickEntry.set_enabled defaults.hijack_right_click_to_enter
                RepeatBehavior.apply defaults.commands_do_not_repeat
                speedText <- format defaults.base_speed
                refresh_status ()

                match Config.read_raw () with
                | Ok(path, content) -> self.ShowRaw(path, content)
                | Error _ -> ()

                self.ShowStatus "Reset to defaults"
            | Error error -> self.ShowStatus $"Could not reset settings: {error}")

        table.ResumeLayout(false)
        self.Controls.Add table
        dismiss_on_background_click self
        self.ResumeLayout(true)

    member _.ApplyTheme() =
        let panel = AppearanceSettings.GetPaintColor PaintColor.PanelBackground
        let text = AppearanceSettings.GetPaintColor PaintColor.TextEnabled
        let edit = AppearanceSettings.GetPaintColor PaintColor.EditBoxBackground

        if appliedTheme <> Some(panel, text, edit) then
            apply_colors self panel text edit
            github.LinkColor <- text
            github.ActiveLinkColor <- text
            github.VisitedLinkColor <- text
            appliedTheme <- Some(panel, text, edit)

    member _.ShowStatus(message: string) =
        configurationText <- message
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
        updateHz.Text <- format config.update_hz
        lensLength.Text <- format config.lens_length_mm_in_mode
        invertMouseX.Checked <- config.invert_mouse_x
        invertMouseY.Checked <- config.invert_mouse_y
        normalizeDiagonal.Checked <- config.normalize_diagonal_movement
        wheelChangesSpeed.Checked <- config.wheel_changes_speed
        exitOnMouseLeft.Checked <- config.exit_on_mouse_left
        exitOnMouseRight.Checked <- config.exit_on_mouse_right
        exitOnMouseMiddle.Checked <- config.exit_on_mouse_middle
        hijackRightClick.Checked <- config.hijack_right_click_to_enter
        commandsDoNotRepeat.Checked <- config.commands_do_not_repeat
        boostHold.Checked <- config.boost_hold_instead_of_toggle
        slowHold.Checked <- config.slow_hold_instead_of_toggle

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
            parse_number "Update rate" updateHz,
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
          Ok updateValue,
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
                  invert_mouse_x = invertMouseX.Checked
                  invert_mouse_y = invertMouseY.Checked
                  update_hz = updateValue
                  normalize_diagonal_movement = normalizeDiagonal.Checked
                  wheel_changes_speed = wheelChangesSpeed.Checked
                  exit_on_mouse_left = exitOnMouseLeft.Checked
                  exit_on_mouse_right = exitOnMouseRight.Checked
                  exit_on_mouse_middle = exitOnMouseMiddle.Checked
                  hijack_right_click_to_enter = hijackRightClick.Checked
                  commands_do_not_repeat = commandsDoNotRepeat.Checked
                  boost_hold_instead_of_toggle = boostHold.Checked
                  slow_hold_instead_of_toggle = slowHold.Checked
                  vertical_speed_multiplier = verticalValue
                  lens_length_mm_in_mode = lensValue }
        | a, b, c, d, e, f, g, h, i, j ->
            [ a; b; c; d; e; f; g; h; i; j ]
            |> List.choose (function
                | Error error -> Some error
                | Ok _ -> None)
            |> String.concat Environment.NewLine
            |> Error
