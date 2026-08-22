namespace RhinosCanFly

open System
open Eto.Drawing
open Eto.Forms
open Rhino
open Rhino.UI

module SettingsDialogPlacement =
    [<Literal>]
    let PREFERRED_WIDTH = 1000

    [<Literal>]
    let PREFERRED_HEIGHT = 1000

    [<Literal>]
    let SCREEN_MARGIN = 24

    let mutable last_location: Point option = None

    let fit_size (minimum: Size) (workingArea: RectangleF) =
        let availableWidth = max minimum.Width (int workingArea.Width - SCREEN_MARGIN * 2)

        let availableHeight =
            max minimum.Height (int workingArea.Height - SCREEN_MARGIN * 2)

        Size(min PREFERRED_WIDTH availableWidth, min PREFERRED_HEIGHT availableHeight)

    let centered_location (bounds: RectangleF) (size: Size) =
        Point(int bounds.X + (int bounds.Width - size.Width) / 2, int bounds.Y + (int bounds.Height - size.Height) / 2)

    let clamped_location (workingArea: RectangleF) (size: Size) (location: Point) =
        let minimumX = int workingArea.X
        let minimumY = int workingArea.Y
        let maximumX = max minimumX (int workingArea.Right - size.Width)
        let maximumY = max minimumY (int workingArea.Bottom - size.Height)

        Point(min maximumX (max minimumX location.X), min maximumY (max minimumY location.Y))

module SettingsScrollPosition =
    let mutable command_dialog = Point.Empty
    let mutable rhino_options = Point.Empty

type RhinosCanFlySettingsDialog() as self =
    inherit
        Dialog(
            Title = "Rhinos Can Fly Options",
            Size = Size(SettingsDialogPlacement.PREFERRED_WIDTH, SettingsDialogPlacement.PREFERRED_HEIGHT),
            MinimumSize = Size(700, 550),
            Resizable = true
        )

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

        self.LoadComplete.Add(fun (_: EventArgs) -> control.SetScrollPosition SettingsScrollPosition.command_dialog)

        self.Closed.Add(fun (_: EventArgs) ->
            SettingsDialogPlacement.last_location <- Some self.Location
            SettingsScrollPosition.command_dialog <- control.ReadScrollPosition())

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

type RhinosCanFlyOptionsPage() =
    inherit OptionsDialogPage "RhinosCanFly"

    let control = lazy (new SettingsControl())
    let mutable inputSuspension: InputSuspensionLease option = None

    let save_scroll_position () =
        if control.IsValueCreated then
            SettingsScrollPosition.rhino_options <- control.Value.ReadScrollPosition()

    let suspend_input () =
        match inputSuspension with
        | Some _ -> Ok()
        | None ->
            match RuntimeSettings.suspend_input () with
            | Ok lease ->
                inputSuspension <- Some lease

                match lease.cleanup_error with
                | None -> Ok()
                | Some cleanupError ->
                    inputSuspension <- None

                    match RuntimeSettings.resume_input lease with
                    | Ok() -> Error $"Input cleanup is incomplete: {cleanupError}"
                    | Error resumeError ->
                        Error $"Input cleanup is incomplete: {cleanupError}; resume failed: {resumeError}"
            | Error error -> Error error

    let resume_input () =
        let suspension = inputSuspension
        inputSuspension <- None

        match suspension with
        | Some lease -> RuntimeSettings.resume_input lease
        | None -> Ok()

    let resume_input_after_options () =
        match resume_input () with
        | Ok() -> true
        | Error error ->
            SettingsUi.report_error $"RhinosCanFly could not resume input after Options: {error}"
            false

    override _.LocalPageTitle = "Rhinos Can Fly"
    override _.PageControl = control.Value

    override _.OnActivate(active: bool) =
        if active then
            match suspend_input () with
            | Error error ->
                SettingsUi.report_error $"RhinosCanFly could not suspend input for Options: {error}"
                false
            | Ok() ->
                try
                    Settings.load control.Value
                    control.Value.SetScrollPosition SettingsScrollPosition.rhino_options
                    true
                with error ->
                    resume_input_after_options () |> ignore
                    SettingsUi.report_error $"RhinosCanFly Options activation failed: {error.Message}"
                    false
        else
            save_scroll_position ()
            let mutable deactivated = true

            try
                if control.IsValueCreated then
                    control.Value.CancelBindingCapture()
            with error ->
                SettingsUi.report_error $"RhinosCanFly Options deactivation failed: {error.Message}"
                deactivated <- false

            deactivated

    override _.OnApply() =
        try
            save_scroll_position ()

            if control.IsValueCreated then
                control.Value.CancelBindingCapture()

                let saved = Settings.save control.Value

                if saved then resume_input_after_options () else false
            else
                resume_input_after_options ()
        with error ->
            SettingsUi.report_error $"RhinosCanFly Options apply failed: {error.Message}"
            false

    override _.OnCancel() =
        try
            save_scroll_position ()

            if control.IsValueCreated then
                control.Value.CancelBindingCapture()
                Settings.load control.Value
        with error ->
            SettingsUi.report_error $"RhinosCanFly Options cancel failed: {error.Message}"

        resume_input_after_options () |> ignore

    override _.OnDefaults() =
        try
            control.Value.CancelBindingCapture()
            control.Value.LoadConfig ConfigSchema.defaults
            control.Value.ClearError()
        with error ->
            SettingsUi.report_error $"RhinosCanFly Options defaults failed: {error.Message}"
