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

type RhinosCanFlySettingsDialog() as self =
    inherit
        Form(
            Text = "Rhinos Can Fly Options",
            StartPosition = FormStartPosition.CenterScreen,
            Size = Size(900, 900),
            MinimumSize = Size(650, 500)
        )

    let control = new SettingsControl(Dock = DockStyle.Fill)
    let saveButton = new Button(Text = "Save", AutoSize = true)

    let cancelButton =
        new Button(Text = "Cancel", AutoSize = true, DialogResult = DialogResult.Cancel)

    let buttons =
        new FlowLayoutPanel(
            Dock = DockStyle.Bottom,
            AutoSize = true,
            FlowDirection = FlowDirection.RightToLeft,
            Padding = Padding 8
        )

    let apply_theme () =
        let background = AppearanceSettings.GetPaintColor PaintColor.PanelBackground
        let text = AppearanceSettings.GetPaintColor PaintColor.TextEnabled
        self.BackColor <- background
        self.ForeColor <- text
        buttons.BackColor <- background

        for button in [ saveButton; cancelButton ] do
            button.UseVisualStyleBackColor <- false
            button.BackColor <- background
            button.ForeColor <- text

    do
        buttons.Controls.Add cancelButton
        buttons.Controls.Add saveButton
        self.Controls.Add control
        self.Controls.Add buttons
        self.AcceptButton <- saveButton
        self.CancelButton <- cancelButton
        apply_theme ()

        saveButton.Click.Add(fun (_: EventArgs) ->
            if Settings.save control then
                self.DialogResult <- DialogResult.OK
                self.Close())

        Settings.load control

type RhinosCanFlyOptionsPage() =
    inherit OptionsDialogPage "RhinosCanFly"

    let control = lazy (new SettingsControl())

    override _.LocalPageTitle = "Rhinos Can Fly"
    override _.PageControl = control.Value

    override _.OnActivate(active: bool) =
        if active then
            Settings.load control.Value

        true

    override _.OnApply() =
        if control.IsValueCreated then
            Settings.save control.Value
        else
            true

    override _.OnCancel() =
        if control.IsValueCreated then
            Settings.load control.Value

    override _.OnDefaults() =
        control.Value.LoadConfig(Config.default_config ())
        control.Value.ShowStatus "Defaults loaded; click OK or Apply to save"
