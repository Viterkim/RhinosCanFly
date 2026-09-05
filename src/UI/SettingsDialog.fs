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

    let fit_size (minimum: Size) (working_area: RectangleF) =
        let available_width = max minimum.Width (int working_area.Width - SCREEN_MARGIN * 2)

        let available_height =
            max minimum.Height (int working_area.Height - SCREEN_MARGIN * 2)

        Size(min PREFERRED_WIDTH available_width, min PREFERRED_HEIGHT available_height)

    let centered_location (bounds: RectangleF) (size: Size) =
        Point(int bounds.X + (int bounds.Width - size.Width) / 2, int bounds.Y + (int bounds.Height - size.Height) / 2)

    let clamped_location (working_area: RectangleF) (size: Size) (location: Point) =
        let minimum_x = int working_area.X
        let minimum_y = int working_area.Y
        let maximum_x = max minimum_x (int working_area.Right - size.Width)
        let maximum_y = max minimum_y (int working_area.Bottom - size.Height)

        Point(min maximum_x (max minimum_x location.X), min maximum_y (max minimum_y location.Y))

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
    let save_button = new Button(Text = "Save")
    let cancel_button = new Button(Text = "Cancel")
    let window_icon = SettingsUi.load_icon ()
    let mutable resources_disposed = false

    do
        window_icon |> Option.iter (fun (icon: Icon) -> self.Icon <- icon)

        let buttons = new TableLayout(Spacing = Size(8, 0))
        let button_row = new TableRow()
        button_row.Cells.Add(new TableCell(new Panel(), true))
        button_row.Cells.Add(new TableCell(save_button, false))
        button_row.Cells.Add(new TableCell(cancel_button, false))
        buttons.Rows.Add button_row

        let layout = new TableLayout(Padding = Padding 8, Spacing = Size(0, 8))
        let control_row = new TableRow()
        control_row.Cells.Add(new TableCell(control, true))
        control_row.ScaleHeight <- true
        layout.Rows.Add control_row
        layout.Rows.Add(new TableRow(new TableCell(buttons, true)))

        self.Content <- layout
        self.DefaultButton <- save_button
        self.AbortButton <- cancel_button
        SettingsUi.use_rhino_style self

        save_button.Click.Add(fun (_: EventArgs) ->
            if Settings.save control then
                self.Close())

        cancel_button.Click.Add(fun (_: EventArgs) -> self.Close())

        self.LoadComplete.Add(fun (_: EventArgs) -> control.SetScrollPosition SettingsScrollPosition.command_dialog)

        self.Closed.Add(fun (_: EventArgs) ->
            SettingsDialogPlacement.last_location <- Some self.Location
            SettingsScrollPosition.command_dialog <- control.ReadScrollPosition())

        Settings.load control

    override _.Dispose(disposing: bool) =
        if disposing && not resources_disposed then
            resources_disposed <- true
            control.Dispose()
            window_icon |> Option.iter (fun (icon: Icon) -> icon.Dispose())

        base.Dispose disposing

    member _.ShowForRhino(document: RhinoDoc) =
        let parent =
            if isNull document then
                RhinoEtoApp.MainWindow
            else
                RhinoEtoApp.MainWindowForDocument document

        let parent_screen =
            if isNull parent || isNull parent.Screen then
                Screen.PrimaryScreen
            else
                parent.Screen

        let screen =
            match SettingsDialogPlacement.last_location with
            | Some saved ->
                let saved_screen = Screen.FromPoint(PointF saved)

                if isNull saved_screen then parent_screen else saved_screen
            | None -> parent_screen

        let working_area = screen.WorkingArea
        self.Size <- SettingsDialogPlacement.fit_size self.MinimumSize working_area

        let location =
            match SettingsDialogPlacement.last_location with
            | Some saved -> saved
            | None -> SettingsDialogPlacement.centered_location working_area self.Size

        self.Location <- SettingsDialogPlacement.clamped_location working_area self.Size location

        self.ShowSemiModal(document, parent)

type RhinosCanFlyOptionsPage() =
    inherit OptionsDialogPage "RhinosCanFly"

    let control = lazy (new SettingsControl())
    let mutable input_suspension: InputSuspensionLease option = None
    let mutable baseline: FlyConfigFile option = None
    let mutable baseline_unavailable = false
    let mutable committed = false

    let copy_viewport_list (source: ViewportNameListFile) =
        { source with
            viewports =
                if isNull source.viewports then
                    Array.empty
                else
                    Array.copy source.viewports }

    let snapshot (source: FlyConfigFile) =
        { source with
            viewport_capabilities = copy_viewport_list source.viewport_capabilities
            right_click_flight_entry = copy_viewport_list source.right_click_flight_entry }

    let capture_baseline () =
        if Option.isNone baseline && not baseline_unavailable then
            match RuntimeSettings.current () with
            | Ok result -> baseline <- Some(snapshot result.config_file)
            | Error _ -> baseline_unavailable <- true

    let save_scroll_position () =
        if control.IsValueCreated then
            SettingsScrollPosition.rhino_options <- control.Value.ReadScrollPosition()

    let suspend_input () =
        match input_suspension with
        | Some _ -> Ok()
        | None ->
            match RuntimeSettings.suspend_input () with
            | Ok lease ->
                input_suspension <- Some lease

                match lease.cleanup_error with
                | None -> Ok()
                | Some cleanup_error ->
                    input_suspension <- None

                    match RuntimeSettings.resume_input lease with
                    | Ok() -> Error $"Input cleanup is incomplete: {cleanup_error}"
                    | Error resume_error ->
                        Error $"Input cleanup is incomplete: {cleanup_error}; resume failed: {resume_error}"
            | Error error -> Error error

    let resume_input () =
        let suspension = input_suspension
        input_suspension <- None

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
                    capture_baseline ()
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

                if saved then
                    committed <- true
                    resume_input_after_options ()
                else
                    false
            else
                resume_input_after_options ()
        with error ->
            SettingsUi.report_error $"RhinosCanFly Options apply failed: {error.Message}"
            false

    override _.OnCancel() =
        let mutable restored = true

        try
            save_scroll_position ()

            if control.IsValueCreated then
                control.Value.CancelBindingCapture()

            if committed then
                match baseline with
                | Some original ->
                    let differs =
                        match RuntimeSettings.current () with
                        | Ok result -> result.config_file <> original
                        | Error _ -> true

                    if differs then
                        match RuntimeSettings.save_and_apply original with
                        | Ok _ -> committed <- false
                        | Error error ->
                            restored <- false
                            SettingsUi.report_error $"RhinosCanFly could not restore settings on cancel: {error}"
                    else
                        committed <- false
                | None ->
                    restored <- false

                    SettingsUi.report_error
                        "RhinosCanFly could not restore settings on cancel: the original configuration was not available"

            if control.IsValueCreated && restored then
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
