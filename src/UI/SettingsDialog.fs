namespace RhinosCanFly

open System
open Eto.Drawing
open Eto.Forms
open Rhino
open Rhino.UI

module SettingsDialogPlacement =
    [<Literal>]
    let preferred_width = 1000

    [<Literal>]
    let preferred_height = 1000

    [<Literal>]
    let screen_margin = 24

    let mutable last_location: Point option = None

    let fit_size (minimum: Size) (workingArea: RectangleF) =
        let availableWidth = max minimum.Width (int workingArea.Width - screen_margin * 2)

        let availableHeight =
            max minimum.Height (int workingArea.Height - screen_margin * 2)

        Size(min preferred_width availableWidth, min preferred_height availableHeight)

    let centered_location (bounds: RectangleF) (size: Size) =
        Point(int bounds.X + (int bounds.Width - size.Width) / 2, int bounds.Y + (int bounds.Height - size.Height) / 2)

    let clamped_location (workingArea: RectangleF) (size: Size) (location: Point) =
        let minimumX = int workingArea.X
        let minimumY = int workingArea.Y
        let maximumX = max minimumX (int workingArea.Right - size.Width)
        let maximumY = max minimumY (int workingArea.Bottom - size.Height)

        Point(min maximumX (max minimumX location.X), min maximumY (max minimumY location.Y))

type RhinosCanFlySettingsDialog() as self =
    inherit
        Dialog(
            Title = "Rhinos Can Fly Options",
            Size = Size(SettingsDialogPlacement.preferred_width, SettingsDialogPlacement.preferred_height),
            MinimumSize = Size(700, 550),
            Resizable = true
        )

    let control = new SettingsControl()
    let saveButton = new Button(Text = "Save")
    let cancelButton = new Button(Text = "Cancel")
    let windowIcon = SettingsUi.load_icon ()
    let mutable resourcesDisposed = false
    let mutable saved = false

    do
        windowIcon |> Option.iter (fun (icon: Icon) -> self.Icon <- icon)

        let buttons = new TableLayout(Spacing = Size(8, 0))
        let buttonRow = new TableRow()
        buttonRow.Cells.Add(new TableCell(new Panel(), true))
        buttonRow.Cells.Add(new TableCell(saveButton, false))
        buttonRow.Cells.Add(new TableCell(cancelButton, false))
        buttons.Rows.Add buttonRow

        let layout = new TableLayout(Padding = Padding 8, Spacing = Size(0, 8))
        let controlRow = new TableRow()
        controlRow.Cells.Add(new TableCell(control, true))
        controlRow.ScaleHeight <- true
        layout.Rows.Add controlRow
        layout.Rows.Add(new TableRow(new TableCell(buttons, true)))

        self.Content <- layout
        self.DefaultButton <- saveButton
        self.AbortButton <- cancelButton
        SettingsUi.use_rhino_style self

        saveButton.Click.Add(fun (_: EventArgs) ->
            if Settings.stage control then
                saved <- true
                self.Close())

        cancelButton.Click.Add(fun (_: EventArgs) ->
            RuntimeSettings.discard_staged ()
            self.Close())

        self.Closed.Add(fun (_: EventArgs) -> SettingsDialogPlacement.last_location <- Some self.Location)

        Settings.load control

    override _.Dispose(disposing: bool) =
        if disposing && not resourcesDisposed then
            resourcesDisposed <- true
            windowIcon |> Option.iter (fun (icon: Icon) -> icon.Dispose())

        base.Dispose disposing

    member _.ShowForRhino(document: RhinoDoc) =
        let parent =
            if isNull document then
                RhinoEtoApp.MainWindow
            else
                RhinoEtoApp.MainWindowForDocument document

        let parentScreen =
            if isNull parent || isNull parent.Screen then
                Screen.PrimaryScreen
            else
                parent.Screen

        let screen =
            match SettingsDialogPlacement.last_location with
            | Some saved ->
                let savedScreen = Screen.FromPoint(PointF saved)

                if isNull savedScreen then parentScreen else savedScreen
            | None -> parentScreen

        let workingArea = screen.WorkingArea
        self.Size <- SettingsDialogPlacement.fit_size self.MinimumSize workingArea

        let location =
            match SettingsDialogPlacement.last_location with
            | Some saved -> saved
            | None -> SettingsDialogPlacement.centered_location workingArea self.Size

        self.Location <- SettingsDialogPlacement.clamped_location workingArea self.Size location

        self.ShowSemiModal(document, parent)

    member _.Saved = saved

type RhinosCanFlyOptionsPage() =
    inherit OptionsDialogPage "RhinosCanFly"

    let control = lazy (new SettingsControl())

    override _.LocalPageTitle = "Rhinos Can Fly"
    override _.PageControl = control.Value

    override _.OnActivate(active: bool) =
        try
            if active then
                Settings.load control.Value
            elif control.IsValueCreated then
                control.Value.CancelBindingCapture()

            true
        with error ->
            SettingsUi.report_error $"RhinosCanFly Options activation failed: {error.Message}"
            false

    override _.OnApply() =
        try
            if control.IsValueCreated then
                control.Value.CancelBindingCapture()

                Settings.stage control.Value
            else
                true
        with error ->
            SettingsUi.report_error $"RhinosCanFly Options apply failed: {error.Message}"
            false

    override _.OnCancel() =
        try
            RuntimeSettings.discard_staged ()

            if control.IsValueCreated then
                control.Value.CancelBindingCapture()
                Settings.load control.Value
        with error ->
            SettingsUi.report_error $"RhinosCanFly Options cancel failed: {error.Message}"

    override _.OnDefaults() =
        try
            control.Value.CancelBindingCapture()
            control.Value.LoadConfig ConfigSchema.defaults
            control.Value.ClearError()
        with error ->
            SettingsUi.report_error $"RhinosCanFly Options defaults failed: {error.Message}"
