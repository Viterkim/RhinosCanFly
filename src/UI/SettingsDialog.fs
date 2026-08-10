namespace RhinosCanFly

open System
open Eto.Drawing
open Eto.Forms
open Rhino
open Rhino.UI

module SettingsDialogPosition =
    let mutable last_location: Point option = None

type RhinosCanFlySettingsDialog() as self =
    inherit
        Dialog(Title = "Rhinos Can Fly Options", Size = Size(990, 990), MinimumSize = Size(650, 500), Resizable = true)

    let control = new SettingsControl()
    let saveButton = new Button(Text = "Save")
    let cancelButton = new Button(Text = "Cancel")
    let windowIcon = SettingsUi.load_icon ()
    let mutable resourcesDisposed = false

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
            if Settings.save control then
                self.Close())

        cancelButton.Click.Add(fun (_: EventArgs) -> self.Close())

        self.Closed.Add(fun (_: EventArgs) -> SettingsDialogPosition.last_location <- Some self.Location)

        Settings.load control

    override _.Dispose(disposing: bool) =
        if disposing && not resourcesDisposed then
            resourcesDisposed <- true
            windowIcon |> Option.iter (fun (icon: Icon) -> icon.Dispose())

        base.Dispose disposing

    member _.ShowForRhino(document: RhinoDoc) =
        SettingsDialogPosition.last_location
        |> Option.iter (fun (location: Point) -> self.Location <- location)

        let parent =
            if isNull document then
                RhinoEtoApp.MainWindow
            else
                RhinoEtoApp.MainWindowForDocument document

        if isNull parent then
            self.ShowModal()
        else
            self.ShowModal parent

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
        control.Value.LoadConfig ConfigSchema.defaults
        control.Value.ClearError()
