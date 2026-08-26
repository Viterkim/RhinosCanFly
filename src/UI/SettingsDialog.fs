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

    let control =
        InputDebugTrace.write "Settings dialog SettingsControl construction begin"
        let value = new SettingsControl()
        InputDebugTrace.write "Settings dialog SettingsControl construction end"
        value

    let saveButton = new Button(Text = "Save")
    let cancelButton = new Button(Text = "Cancel")
    let windowIcon = SettingsUi.load_icon ()
    let mutable resourcesDisposed = false

    do
        InputDebugTrace.write "Settings dialog layout begin"
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
            InputDebugTrace.write "Settings dialog Save click begin"

            if Settings.save control then
                InputDebugTrace.write "Settings dialog Save click saved=true close begin"
                self.Close()
                InputDebugTrace.write "Settings dialog Save click close end"
            else
                InputDebugTrace.write "Settings dialog Save click saved=false")

        cancelButton.Click.Add(fun (_: EventArgs) ->
            InputDebugTrace.write "Settings dialog Cancel click close begin"
            self.Close()
            InputDebugTrace.write "Settings dialog Cancel click close end")

        self.LoadComplete.Add(fun (_: EventArgs) ->
            InputDebugTrace.write "Settings dialog LoadComplete begin"
            control.SetScrollPosition SettingsScrollPosition.command_dialog
            InputDebugTrace.write "Settings dialog LoadComplete end")

        self.Closed.Add(fun (_: EventArgs) ->
            InputDebugTrace.write "Settings dialog Closed begin"
            SettingsDialogPlacement.last_location <- Some self.Location
            SettingsScrollPosition.command_dialog <- control.ReadScrollPosition()
            InputDebugTrace.write "Settings dialog Closed end")

        InputDebugTrace.write "Settings dialog Settings.load begin"
        Settings.load control
        InputDebugTrace.write "Settings dialog Settings.load end"
        InputDebugTrace.write "Settings dialog layout end"

    override _.Dispose(disposing: bool) =
        InputDebugTrace.write
            $"Settings dialog Dispose begin disposing={disposing} resources-disposed={resourcesDisposed}"

        if disposing && not resourcesDisposed then
            resourcesDisposed <- true
            control.Dispose()
            windowIcon |> Option.iter (fun (icon: Icon) -> icon.Dispose())

        base.Dispose disposing
        InputDebugTrace.write "Settings dialog Dispose end"

    member _.ShowForRhino(document: RhinoDoc) =
        InputDebugTrace.write "Settings dialog ShowForRhino placement begin"

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

        InputDebugTrace.write
            $"Settings dialog ShowSemiModal begin x={self.Location.X} y={self.Location.Y} width={self.Size.Width} height={self.Size.Height}"

        self.ShowSemiModal(document, parent)
        InputDebugTrace.write "Settings dialog ShowSemiModal end"

type RhinosCanFlyOptionsPage() =
    inherit OptionsDialogPage "RhinosCanFly"

    let control = lazy (new SettingsControl())
    let mutable inputSuspension: InputSuspensionLease option = None

    let save_scroll_position () =
        if control.IsValueCreated then
            SettingsScrollPosition.rhino_options <- control.Value.ReadScrollPosition()

    let suspend_input () =
        InputDebugTrace.write $"Rhino Options page suspend begin existing-lease={inputSuspension.IsSome}"

        match inputSuspension with
        | Some _ ->
            InputDebugTrace.write "Rhino Options page suspend end reused=true result=ok"
            Ok()
        | None ->
            match RuntimeSettings.suspend_input () with
            | Ok lease ->
                inputSuspension <- Some lease

                match lease.cleanup_error with
                | None ->
                    InputDebugTrace.write $"Rhino Options page suspend end lease={lease.id} result=ok"
                    Ok()
                | Some cleanupError ->
                    inputSuspension <- None

                    match RuntimeSettings.resume_input lease with
                    | Ok() ->
                        InputDebugTrace.write
                            $"Rhino Options page suspend end lease={lease.id} result=cleanup-error error={cleanupError}"

                        Error $"Input cleanup is incomplete: {cleanupError}"
                    | Error resumeError ->
                        InputDebugTrace.write
                            $"Rhino Options page suspend end lease={lease.id} result=cleanup-and-resume-error cleanup={cleanupError} resume={resumeError}"

                        Error $"Input cleanup is incomplete: {cleanupError}; resume failed: {resumeError}"
            | Error error ->
                InputDebugTrace.write $"Rhino Options page suspend end result=error error={error}"
                Error error

    let resume_input () =
        let suspension = inputSuspension
        inputSuspension <- None

        InputDebugTrace.write $"Rhino Options page resume begin had-lease={suspension.IsSome}"

        match suspension with
        | Some lease ->
            let result = RuntimeSettings.resume_input lease
            InputDebugTrace.write $"Rhino Options page resume end lease={lease.id} result={result}"
            result
        | None ->
            InputDebugTrace.write "Rhino Options page resume end result=ok no-lease=true"
            Ok()

    let resume_input_after_options () =
        match resume_input () with
        | Ok() -> true
        | Error error ->
            SettingsUi.report_error $"RhinosCanFly could not resume input after Options: {error}"
            false

    override _.LocalPageTitle = "Rhinos Can Fly"
    override _.PageControl = control.Value

    override _.OnActivate(active: bool) =
        InputDebugTrace.write $"Rhino Options page OnActivate begin active={active}"

        if active then
            match suspend_input () with
            | Error error ->
                SettingsUi.report_error $"RhinosCanFly could not suspend input for Options: {error}"
                false
            | Ok() ->
                try
                    Settings.load control.Value
                    control.Value.SetScrollPosition SettingsScrollPosition.rhino_options
                    InputDebugTrace.write "Rhino Options page OnActivate end active=true result=true"
                    true
                with error ->
                    resume_input_after_options () |> ignore
                    SettingsUi.report_error $"RhinosCanFly Options activation failed: {error.Message}"
                    InputDebugTrace.write $"Rhino Options page OnActivate end active=true result=false error={error}"
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

            InputDebugTrace.write $"Rhino Options page OnActivate end active=false result={deactivated}"
            deactivated

    override _.OnApply() =
        InputDebugTrace.write "Rhino Options page OnApply begin"

        try
            save_scroll_position ()

            if control.IsValueCreated then
                control.Value.CancelBindingCapture()

                let saved = Settings.save control.Value

                let result = if saved then resume_input_after_options () else false
                InputDebugTrace.write $"Rhino Options page OnApply end result={result} saved={saved}"
                result
            else
                let result = resume_input_after_options ()
                InputDebugTrace.write $"Rhino Options page OnApply end result={result} control-created=false"
                result
        with error ->
            SettingsUi.report_error $"RhinosCanFly Options apply failed: {error.Message}"
            InputDebugTrace.write $"Rhino Options page OnApply end result=false error={error}"
            false

    override _.OnCancel() =
        InputDebugTrace.write "Rhino Options page OnCancel begin"

        try
            save_scroll_position ()

            if control.IsValueCreated then
                control.Value.CancelBindingCapture()
                Settings.load control.Value
        with error ->
            SettingsUi.report_error $"RhinosCanFly Options cancel failed: {error.Message}"

        resume_input_after_options () |> ignore
        InputDebugTrace.write "Rhino Options page OnCancel end"

    override _.OnDefaults() =
        InputDebugTrace.write "Rhino Options page OnDefaults begin"

        try
            control.Value.CancelBindingCapture()
            control.Value.LoadConfig ConfigSchema.defaults
            control.Value.ClearError()
        with error ->
            SettingsUi.report_error $"RhinosCanFly Options defaults failed: {error.Message}"

        InputDebugTrace.write "Rhino Options page OnDefaults end"
