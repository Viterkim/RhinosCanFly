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
    let invertMouseY = new CheckBox(Text = "Invert mouse Y")

    let normalizeDiagonal =
        new CheckBox(Text = "Normalize diagonal movement (Normal FPS game behavior)")

    let wheelChangesSpeed = new CheckBox(Text = "Mouse wheel up/down increases/decreases speed")

    let exitOnMouseLeft = new CheckBox(Text = "Left mouse button exits fly mode")
    let exitOnMouseRight = new CheckBox(Text = "Right mouse button exits fly mode")
    let exitOnMouseMiddle = new CheckBox(Text = "Middle mouse button exits fly mode")

    let hijackRightClick =
        new CheckBox(Text = "Hijack right click in perspective views when no command is running")

    let boostHold = new CheckBox(Text = "Boost Mode: hold instead of toggle")
    let slowHold = new CheckBox(Text = "Slow Mode: hold instead of toggle")
    let status = new Label(AutoSize = true, MaximumSize = Size(700, 0))
    let runtimeSpeed = new Label(AutoSize = true)
    let runtimeLens = new Label(AutoSize = true)
    let configPath = new TextBox(ReadOnly = true)

    let rawJson =
        new TextBox(ReadOnly = true, Multiline = true, WordWrap = false, ScrollBars = ScrollBars.Both, Height = 220)

    let resetAll = new Button(Text = "Reset all to defaults", AutoSize = true)
    let github = new LinkLabel(Text = "Viterkim/RhinosCanFly", AutoSize = true)

    let format (value: float) =
        let rounded = Math.Round(value, 9, MidpointRounding.AwayFromZero)
        rounded.ToString("G15", CultureInfo.InvariantCulture)

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

    let binding_editor (field: TextBox) (allow_empty: bool) (default_value: string) =
        let column_count = if allow_empty then 4 else 3

        let panel =
            new TableLayoutPanel(Dock = DockStyle.Fill, AutoSize = true, ColumnCount = column_count)

        let small_button (text: string) =
            new Button(Text = text, AutoSize = false, Size = Size(66, 24), Anchor = AnchorStyles.Left)

        let setButton = small_button "Set..."
        let defaultButton = small_button "Default"
        let clearButton = small_button "Clear"
        let mutable capturing = false
        field.ReadOnly <- true
        field.Dock <- DockStyle.Fill
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100.f)) |> ignore
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize)) |> ignore
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize)) |> ignore

        if allow_empty then
            panel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize)) |> ignore

        panel.Controls.Add(field, 0, 0)
        panel.Controls.Add(setButton, 1, 0)
        panel.Controls.Add(defaultButton, 2, 0)

        if allow_empty then
            panel.Controls.Add(clearButton, 3, 0)

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
                field.Text <- key_name event.KeyCode
                stop ())

        setButton.MouseDown.Add(fun (event: MouseEventArgs) ->
            if capturing then
                match mouse_name event.Button with
                | Some binding ->
                    field.Text <- binding
                    stop ()
                | None -> ())

        setButton.LostFocus.Add(fun (_: EventArgs) ->
            if capturing then
                stop ())

        defaultButton.Click.Add(fun (_: EventArgs) -> field.Text <- default_value)
        clearButton.Click.Add(fun (_: EventArgs) -> field.Text <- "")
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

    let add_checkbox (control: CheckBox) =
        let row = table.RowCount
        table.RowCount <- row + 1
        control.AutoSize <- true
        control.Margin <- Padding(3, 5, 3, 3)
        table.Controls.Add(control, 0, row)
        table.SetColumnSpan(control, 2)

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

    do
        self.AutoScroll <- true
        self.Controls.Add table

        let title =
            new Label(
                Text = "Rhinos Can Fly",
                AutoSize = true,
                Font = new Font(self.Font.FontFamily, self.Font.Size + 5.f, FontStyle.Bold)
            )

        add_full_width title
        add_note "Free-flight viewport controls and configuration"
        add_heading "Behavior"
        add_checkbox exitOnMouseLeft
        add_checkbox exitOnMouseRight
        add_checkbox exitOnMouseMiddle
        add_checkbox hijackRightClick
        add_checkbox boostHold
        add_checkbox slowHold
        add_checkbox invertMouseY
        add_checkbox normalizeDiagonal
        add_checkbox wheelChangesSpeed
        add_heading "Controls"

        add_note
            "Click Set, then press a keyboard key or mouse button. Speed increase/decrease can be cleared. Mouse wheel behavior is configured above."

        add_row "Forward" (binding_editor forward false defaults.forward)
        add_row "Backward" (binding_editor backward false defaults.backward)
        add_row "Move left" (binding_editor left false defaults.left)
        add_row "Move right" (binding_editor right false defaults.right)
        add_row "Move up" (binding_editor up false defaults.up)
        add_row "Move down" (binding_editor down false defaults.down)
        add_row "Boost Mode (Toggle)" (binding_editor boost false defaults.boost_toggle)
        add_row "Slow Mode (Toggle)" (binding_editor slow false defaults.slow)
        add_row "Increase speed" (binding_editor speedIncrease true defaults.speed_increase)
        add_row "Decrease speed" (binding_editor speedDecrease true defaults.speed_decrease)
        add_row "Exit fly mode" (binding_editor exitKey false defaults.exit_key)

        add_note
            "Mouse wheel up/down is reserved for speed adjustment and is not a bindable key. Equals means the =/+ key; Minus means the -/_ key."

        add_heading "Speed and mouse"
        add_row "Base speed (document units/second)" baseSpeed
        add_row "Minimum speed" minimumSpeed
        add_row "Maximum speed" maximumSpeed
        add_row "Speed step multiplier" speedStep
        add_row "Boost multiplier" boostMultiplier
        add_row "Slow multiplier" slowMultiplier
        add_row "Move up/down multiplier" verticalSpeedMultiplier
        add_row "Mouse sensitivity (15 = default)" mouseSensitivity
        add_note "Lower is slower, higher is faster. Values can range from tiny decimals such as 0.01 to 100 or more."
        add_row "Update rate (Hz)" updateHz
        add_row "Lens length in fly mode (mm)" lensLength

        add_note
            "Set to 0 to keep the viewport lens unchanged. A positive value is temporary; the original lens is restored on exit."

        add_heading "Raw JSON configuration"
        add_row "File" configPath
        add_row "Contents" rawJson
        add_heading "Status"
        add_row "Configuration" status
        add_heading "Runtime State"
        add_row "Current speed" runtimeSpeed
        add_row "Current lens" runtimeLens
        let version = Assembly.GetExecutingAssembly().GetName().Version
        add_note $"Version {version.Major}.{version.Minor}.{version.Build}"

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
                runtimeSpeed.Text <- format defaults.base_speed

                match Config.read_raw () with
                | Ok(path, content) -> self.ShowRaw(path, content)
                | Error _ -> ()

                self.ShowStatus "Reset all settings to defaults"
            | Error error -> self.ShowStatus $"Could not reset settings: {error}")

    member _.ApplyTheme() =
        let panel = AppearanceSettings.GetPaintColor PaintColor.PanelBackground
        let text = AppearanceSettings.GetPaintColor PaintColor.TextEnabled
        let edit = AppearanceSettings.GetPaintColor PaintColor.EditBoxBackground
        apply_colors self panel text edit
        github.LinkColor <- text
        github.ActiveLinkColor <- text
        github.VisitedLinkColor <- text

    member _.ShowStatus(message: string) = status.Text <- message

    member _.ShowRuntimeState(speed: float, lens: float option) =
        runtimeSpeed.Text <- format speed

        runtimeLens.Text <-
            match lens with
            | Some value -> $"{format value} mm"
            | None -> "Unavailable"

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
        invertMouseY.Checked <- config.invert_mouse_y
        normalizeDiagonal.Checked <- config.normalize_diagonal_movement
        wheelChangesSpeed.Checked <- config.wheel_changes_speed
        exitOnMouseLeft.Checked <- config.exit_on_mouse_left
        exitOnMouseRight.Checked <- config.exit_on_mouse_right
        exitOnMouseMiddle.Checked <- config.exit_on_mouse_middle
        hijackRightClick.Checked <- config.hijack_right_click_to_enter
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
                  invert_mouse_y = invertMouseY.Checked
                  update_hz = updateValue
                  normalize_diagonal_movement = normalizeDiagonal.Checked
                  wheel_changes_speed = wheelChangesSpeed.Checked
                  exit_on_mouse_left = exitOnMouseLeft.Checked
                  exit_on_mouse_right = exitOnMouseRight.Checked
                  exit_on_mouse_middle = exitOnMouseMiddle.Checked
                  hijack_right_click_to_enter = hijackRightClick.Checked
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
