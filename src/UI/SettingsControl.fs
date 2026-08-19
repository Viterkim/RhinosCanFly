namespace RhinosCanFly

open System
open System.Diagnostics
open System.Globalization
open System.Reflection
open Eto.Drawing
open Eto.Forms

type SettingsControl() as self =
    inherit Panel()

    let defaults = ConfigSchema.defaults
    let fields = SettingsFields.create ()
    let bindings = fields.config.bindings
    let numbers = fields.config.numbers
    let modes = fields.config.modes
    let options = fields.config.options
    let status = fields.status
    let rawJson = fields.raw_json
    let actions = fields.actions
    let version = Assembly.GetExecutingAssembly().GetName().Version
    let versionText = $"{version.Major}.{version.Minor}.{version.Build}"
    let mutable configurationError: string option = None
    let mutable speedText = "Unavailable"
    let mutable lensText = "Unavailable"
    let optionsIcon = SettingsUi.load_icon ()
    let bindingCapture = BindingCapture.create ()
    let mutable resourcesDisposed = false

    let format_runtime_number (value: float) =
        value.ToString("0.######", CultureInfo.InvariantCulture)

    let refresh_status () =
        status.status_line.Text <-
            match configurationError with
            | Some error -> $"Version: {versionText}  |  Configuration error: {error}"
            | None -> $"Version: {versionText}"

        status.runtime_line.Text <- $"Current Speed: {speedText}  |  Current Lens: {lensText}"

    let refresh_side_button_flight_controls () =
        options.mouse4_pivot_in_flight.Enabled <-
            SettingsFields.selected_mode modes.mouse4_pivot_mode <> MouseButtonPivotMode.Off

        options.mouse5_pivot_in_flight.Enabled <-
            SettingsFields.selected_mode modes.mouse5_pivot_mode <> MouseButtonPivotMode.Off

    let refresh_view_target_controls () =
        let enabled =
            SettingsFields.selected_mode modes.view_target_mode <> ViewTargetMode.Off

        numbers.view_target_distance_multiplier.Enabled <- enabled
        options.set_view_target_on_restored_flights.Enabled <- enabled

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

        layout.Rows.Add(SettingsLayout.row [ new TableCell(pathLabel, false); new TableCell(rawJson.path, true) ])

        layout.Rows.Add(
            SettingsLayout.row [ new TableCell(contentsLabel, false); new TableCell(rawJson.contents, true) ]
        )

        layout

    let rawJsonPanel = new Panel(Content = rawJsonLayout, Visible = false)

    let refresh_raw () =
        match ConfigStorage.read_raw () with
        | Ok(path, content) ->
            rawJson.path.Text <- path
            rawJson.contents.Text <- content
        | Error error ->
            rawJson.path.Text <- ConfigStorage.path ()
            rawJson.contents.Text <- $"Could not read config: {error}"

    let refresh_raw_if_visible () =
        if rawJsonPanel.Visible then
            refresh_raw ()

    let mainTable = new TableLayout(Padding = Padding 12, Spacing = Size(0, 4))

    do
        mainTable.Rows.Add(SettingsLayout.full_width (title_row ()))
        mainTable.Rows.Add(SettingsLayout.full_width (SettingsLayout.heading "General behaviour"))

        SettingsLayout.grid
            2
            [ options.enabled
              status.runtime_enabled
              options.commands_do_not_repeat
              options.save_speed_to_document
              options.load_speed_from_document
              options.normalize_diagonal_movement ]
        |> SettingsLayout.full_width
        |> mainTable.Rows.Add

        mainTable.Rows.Add(SettingsLayout.full_width (SettingsLayout.heading "Non flying behaviour"))

        SettingsLayout.grid
            2
            [ SettingsLayout.item "Right click" modes.right_click_entry_mode.control
              SettingsLayout.item "Default flight mode" modes.default_flight_mode.control
              SettingsLayout.item "Shift + right click" modes.shift_right_click_mode.control
              SettingsLayout.item "Alt + right click" modes.alt_right_click_mode.control ]
        |> SettingsLayout.full_width
        |> mainTable.Rows.Add

        mainTable.Rows.Add(SettingsLayout.full_width (SettingsLayout.heading "While flying"))

        SettingsLayout.grid
            2
            [ options.exit_on_mouse_right
              options.exit_on_mouse_left
              options.hide_gumball_while_flying
              options.flight_pivot_uses_gumball ]
        |> SettingsLayout.full_width
        |> mainTable.Rows.Add

        mainTable.Rows.Add(SettingsLayout.full_width (SettingsLayout.heading "Target"))

        SettingsLayout.grid
            2
            [ SettingsLayout.item "Mode" modes.view_target_mode.control
              SettingsLayout.item "Distance multiplier" numbers.view_target_distance_multiplier
              options.set_view_target_on_restored_flights ]
        |> SettingsLayout.full_width
        |> mainTable.Rows.Add

        mainTable.Rows.Add(
            SettingsLayout.full_width (
                SettingsLayout.note
                    "Used when flight ends or pivot or pan starts (distance is affected by flight speed and multiplier)"
            )
        )

        mainTable.Rows.Add(SettingsLayout.full_width (SettingsLayout.spacer 2))

        SettingsLayout.grid
            2
            [ SettingsLayout.item "Mouse X" modes.mouse_x_mode.control
              SettingsLayout.item "Mouse Y" modes.mouse_y_mode.control
              SettingsLayout.item "Middle mouse" modes.middle_mouse_while_flying.control
              SettingsLayout.item "Mouse sensitivity" numbers.mouse_sensitivity ]
        |> SettingsLayout.full_width
        |> mainTable.Rows.Add

        mainTable.Rows.Add(SettingsLayout.full_width (SettingsLayout.heading "Mouse 4/5 behaviour"))

        SettingsLayout.grid
            2
            [ SettingsLayout.item "Mouse 4" modes.mouse4_pivot_mode.control
              options.mouse4_pivot_in_flight
              SettingsLayout.item "Mouse 5" modes.mouse5_pivot_mode.control
              options.mouse5_pivot_in_flight ]
        |> SettingsLayout.full_width
        |> mainTable.Rows.Add

        mainTable.Rows.Add(SettingsLayout.full_width (SettingsLayout.spacer 2))

        mainTable.Rows.Add(
            SettingsLayout.full_width (
                SettingsLayout.note
                    "Toggle pivots stop with the same button. While flying uses the same hold or toggle mode when enabled."
            )
        )

        mainTable.Rows.Add(SettingsLayout.full_width (SettingsLayout.heading "Controls"))

        SettingsLayout.grid
            2
            [ SettingsLayout.item "Forward" (binding_editor bindings.forward defaults.forward)
              SettingsLayout.item "Backward" (binding_editor bindings.backward defaults.backward)
              SettingsLayout.item "Move left" (binding_editor bindings.left defaults.left)
              SettingsLayout.item "Move right" (binding_editor bindings.right defaults.right)
              SettingsLayout.item "Move up" (binding_editor bindings.up defaults.up)
              SettingsLayout.item "Move down" (binding_editor bindings.down defaults.down)
              SettingsLayout.item "Pivot left" (binding_editor bindings.key_pivot_left defaults.key_pivot_left)
              SettingsLayout.item "Pivot right" (binding_editor bindings.key_pivot_right defaults.key_pivot_right)
              SettingsLayout.item "Toggle pivot" (binding_editor bindings.pivot_toggle defaults.pivot_toggle)
              SettingsLayout.item "Hold pivot" (binding_editor bindings.pivot_hold defaults.pivot_hold)
              SettingsLayout.item "Toggle pan" (binding_editor bindings.pan_toggle defaults.pan_toggle)
              SettingsLayout.item "Hold pan" (binding_editor bindings.pan_hold defaults.pan_hold)
              SettingsLayout.item "Exit" (binding_editor bindings.exit_key defaults.exit_key)
              SettingsLayout.item
                  "Cancel flight and go back"
                  (binding_editor bindings.cancel_flight_and_restore defaults.cancel_flight_and_restore) ]
        |> SettingsLayout.full_width
        |> mainTable.Rows.Add

        mainTable.Rows.Add(SettingsLayout.full_width (SettingsLayout.heading "Movement speed and controls"))
        mainTable.Rows.Add(SettingsLayout.full_width (SettingsLayout.subheading "Options"))

        SettingsLayout.grid
            2
            [ SettingsLayout.item "Boost behaviour" modes.boost_mode.control
              SettingsLayout.item "Slow behaviour" modes.slow_mode.control
              SettingsLayout.item "MWheel changes speed" modes.wheel_speed_mode.control ]
        |> SettingsLayout.full_width
        |> mainTable.Rows.Add

        mainTable.Rows.Add(SettingsLayout.full_width (SettingsLayout.subheading "Binds"))

        SettingsLayout.grid
            2
            [ SettingsLayout.item "Boost" (binding_editor bindings.boost defaults.boost)
              SettingsLayout.item "Slow" (binding_editor bindings.slow defaults.slow)
              SettingsLayout.item "Increase speed" (binding_editor bindings.speed_increase defaults.speed_increase)
              SettingsLayout.item "Decrease speed" (binding_editor bindings.speed_decrease defaults.speed_decrease) ]
        |> SettingsLayout.full_width
        |> mainTable.Rows.Add

        mainTable.Rows.Add(SettingsLayout.full_width (SettingsLayout.subheading "Values"))

        SettingsLayout.grid
            2
            [ SettingsLayout.item "Base speed" numbers.base_speed
              SettingsLayout.item "Minimum speed" numbers.minimum_speed
              SettingsLayout.item "Maximum speed" numbers.maximum_speed
              SettingsLayout.item "Speed step multiplier" numbers.speed_step_multiplier
              SettingsLayout.item "Boost multiplier" numbers.boost_multiplier
              SettingsLayout.item "Slow multiplier" numbers.slow_multiplier
              SettingsLayout.item "Move up/down multiplier" numbers.vertical_speed_multiplier
              SettingsLayout.item "Key pivot speed multiplier" numbers.key_pivot_speed_multiplier
              SettingsLayout.item "Pivot multiplier" numbers.mouse_pivot_multiplier
              SettingsLayout.item "Pan multiplier" numbers.mouse_pan_multiplier ]
        |> SettingsLayout.full_width
        |> mainTable.Rows.Add

        mainTable.Rows.Add(SettingsLayout.full_width (SettingsLayout.heading "Viewport"))

        SettingsLayout.grid
            2
            [ SettingsLayout.item "Force lens length" numbers.forced_lens_length_mm
              SettingsLayout.item "Force lens length delta" numbers.lens_length_delta_mm ]
        |> SettingsLayout.full_width
        |> mainTable.Rows.Add

        mainTable.Rows.Add(
            SettingsLayout.full_width (SettingsLayout.item "Redraw mode" modes.viewport_paint_mode.control)
        )

        mainTable.Rows.Add(
            SettingsLayout.full_width (SettingsLayout.note "Try an alternative mode only if fly mode is not smooth.")
        )

        mainTable.Rows.Add(SettingsLayout.full_width (SettingsLayout.heading "Status"))
        refresh_status ()
        mainTable.Rows.Add(SettingsLayout.full_width status.status_line)
        mainTable.Rows.Add(SettingsLayout.full_width status.runtime_line)
        mainTable.Rows.Add(SettingsLayout.full_width actions.github)
        mainTable.Rows.Add(SettingsLayout.full_width actions.reset_all)
        mainTable.Rows.Add(SettingsLayout.full_width actions.raw_json_toggle)
        mainTable.Rows.Add(SettingsLayout.full_width rawJsonPanel)

        let scrollable =
            new Scrollable(Border = BorderType.None, ExpandContentWidth = true, Content = mainTable)

        let host = new TableLayout(Spacing = Size.Empty)
        host.Rows.Add(SettingsLayout.row [ new TableCell(bindingCapture.focus_sink, false) ])

        let contentRow = SettingsLayout.row [ new TableCell(scrollable, true) ]
        contentRow.ScaleHeight <- true
        host.Rows.Add contentRow

        self.Content <- host

        modes.mouse4_pivot_mode.control.SelectedIndexChanged.Add(fun (_: EventArgs) ->
            refresh_side_button_flight_controls ())

        modes.mouse5_pivot_mode.control.SelectedIndexChanged.Add(fun (_: EventArgs) ->
            refresh_side_button_flight_controls ())

        modes.view_target_mode.control.SelectedIndexChanged.Add(fun (_: EventArgs) -> refresh_view_target_controls ())

        refresh_side_button_flight_controls ()
        refresh_view_target_controls ()

        actions.raw_json_toggle.Click.Add(fun (_: EventArgs) ->
            rawJsonPanel.Visible <- not rawJsonPanel.Visible

            refresh_raw_if_visible ()

            actions.raw_json_toggle.Text <-
                if rawJsonPanel.Visible then
                    "Hide raw JSON configuration"
                else
                    "Show raw JSON configuration")

        actions.github.Click.Add(fun (_: EventArgs) ->
            try
                let launchedProcess =
                    Process.Start(ProcessStartInfo("https://github.com/Viterkim/RhinosCanFly", UseShellExecute = true))

                if not (isNull launchedProcess) then
                    launchedProcess.Dispose()
            with error ->
                self.ShowError $"Could not open GitHub: {error.Message}")

        actions.reset_all.Click.Add(fun (_: EventArgs) ->
            self.LoadConfig defaults
            self.ClearError())

        BindingCapture.attach_mouse_behavior bindingCapture self
        SettingsUi.use_rhino_style self
        self.UnLoad.Add(fun (_: EventArgs) -> BindingCapture.cancel bindingCapture)

    override _.Dispose(disposing: bool) =
        if disposing && not resourcesDisposed then
            resourcesDisposed <- true
            BindingCapture.dispose bindingCapture
            optionsIcon |> Option.iter (fun (icon: Icon) -> icon.Dispose())

        base.Dispose disposing

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

    member _.ShowRuntimeEnabled(enabled: bool) =
        status.runtime_enabled.Checked <- Nullable enabled

    member _.RefreshRawIfVisible() = refresh_raw_if_visible ()

    member _.CancelBindingCapture() = BindingCapture.cancel bindingCapture

    member _.LoadConfig(config: FlyConfigFile) =
        SettingsConfig.load fields.config config
        refresh_side_button_flight_controls ()
        refresh_view_target_controls ()

    member _.ReadConfig() = SettingsConfig.read fields.config
