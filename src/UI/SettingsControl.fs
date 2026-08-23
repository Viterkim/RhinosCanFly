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
    let parallelViewNames = fields.config.parallel_view_names
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

    let refresh_retarget_controls () =
        let shiftEnabled =
            SettingsFields.selected_mode modes.shift_right_click_action = MouseGestureAction.Retarget

        let altEnabled =
            SettingsFields.selected_mode modes.alt_right_click_action = MouseGestureAction.Retarget

        let ctrlEnabled =
            SettingsFields.selected_mode modes.ctrl_right_click_action = MouseGestureAction.Retarget

        let mouse4Enabled =
            SettingsFields.selected_mode modes.mouse4_action = MouseGestureAction.Retarget

        let mouse5Enabled =
            SettingsFields.selected_mode modes.mouse5_action = MouseGestureAction.Retarget

        modes.shift_right_click_retarget.control.Enabled <- shiftEnabled
        modes.alt_right_click_retarget.control.Enabled <- altEnabled
        modes.ctrl_right_click_retarget.control.Enabled <- ctrlEnabled
        modes.mouse4_retarget.control.Enabled <- mouse4Enabled
        modes.mouse5_retarget.control.Enabled <- mouse5Enabled

        options.mouse4_action_while_flying.Enabled <-
            SettingsFields.selected_mode modes.mouse4_action <> MouseGestureAction.Off

        options.mouse5_action_while_flying.Enabled <-
            SettingsFields.selected_mode modes.mouse5_action <> MouseGestureAction.Off

        let fallbackEnabled =
            (shiftEnabled
             && RetargetMode.uses_distance (SettingsFields.selected_mode modes.shift_right_click_retarget))
            || (altEnabled
                && RetargetMode.uses_distance (SettingsFields.selected_mode modes.alt_right_click_retarget))
            || (ctrlEnabled
                && RetargetMode.uses_distance (SettingsFields.selected_mode modes.ctrl_right_click_retarget))
            || (mouse4Enabled
                && RetargetMode.uses_distance (SettingsFields.selected_mode modes.mouse4_retarget))
            || (mouse5Enabled
                && RetargetMode.uses_distance (SettingsFields.selected_mode modes.mouse5_retarget))
            || RetargetMode.uses_distance (SettingsFields.selected_mode modes.retarget_on_pivot)
            || RetargetMode.uses_distance (SettingsFields.selected_mode modes.retarget_on_pan)
            || RetargetMode.uses_distance (SettingsFields.selected_mode modes.retarget_on_flight_exit)
            || RetargetMode.uses_distance (SettingsFields.selected_mode modes.retarget_on_restored_flight_exit)

        numbers.perspective_retarget_fallback_multiplier.Enabled <- fallbackEnabled
        numbers.parallel_retarget_fallback_multiplier.Enabled <- fallbackEnabled

    let refresh_wheel_speed_controls () =
        options.wheel_changes_speed_during_flight_navigation.Enabled <-
            SettingsFields.selected_mode modes.wheel_speed_mode <> MouseWheelSpeedMode.Off

    let refresh_parallel_view_controls () =
        let mode = SettingsFields.selected_mode modes.parallel_view_flying

        let parallelFlyingPossible = mode <> ParallelViewFlyingMode.DisabledAll

        let usesList =
            mode = ParallelViewFlyingMode.EnabledSome
            || mode = ParallelViewFlyingMode.DisabledSome

        parallelViewNames.Enabled <- usesList
        bindings.toggle_projection.Enabled <- parallelFlyingPossible

        numbers.parallel_mouse_sensitivity.Enabled <- parallelFlyingPossible

        numbers.parallel_mouse_pivot_multiplier.Enabled <- parallelFlyingPossible

        numbers.parallel_mouse_pan_multiplier.Enabled <- parallelFlyingPossible

        numbers.parallel_zoom_speed_multiplier.Enabled <- parallelFlyingPossible

        numbers.parallel_up_down_multiplier.Enabled <- parallelFlyingPossible

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

    let parallelLensLayout =
        let layout = new TableLayout(Spacing = Size(16, 0))

        let description =
            SettingsLayout.note
                "When switching parallel -> perspective we don't have the lens, manually set a non zero value:"

        numbers.perspective_lens_length_after_parallel_mm.Width <- 220

        layout.Rows.Add(
            SettingsLayout.row
                [ new TableCell(description, true)
                  new TableCell(numbers.perspective_lens_length_after_parallel_mm, false) ]
        )

        layout :> Control

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

    let scrollable =
        new Scrollable(Border = BorderType.None, ExpandContentWidth = true, Content = mainTable)

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

        mainTable.Rows.Add(SettingsLayout.full_width (SettingsLayout.heading "Mouse actions"))

        SettingsLayout.grid
            2
            [ SettingsLayout.item "Right click" modes.right_click_entry_mode.control
              SettingsLayout.item "Default flight mode" modes.default_flight_mode.control
              SettingsLayout.item "Shift + right click" modes.shift_right_click_action.control
              SettingsLayout.item "Alt + right click" modes.alt_right_click_action.control
              SettingsLayout.item "Ctrl + right click" modes.ctrl_right_click_action.control ]
        |> SettingsLayout.full_width
        |> mainTable.Rows.Add

        mainTable.Rows.Add(SettingsLayout.full_width (SettingsLayout.subheading "Mouse 4/5"))

        SettingsLayout.grid
            2
            [ SettingsLayout.item "Mouse 4" modes.mouse4_action.control
              options.mouse4_action_while_flying
              SettingsLayout.item "Mouse 5" modes.mouse5_action.control
              options.mouse5_action_while_flying ]
        |> SettingsLayout.full_width
        |> mainTable.Rows.Add

        mainTable.Rows.Add(SettingsLayout.full_width (SettingsLayout.subheading "Sensitivity"))

        SettingsLayout.grid
            2
            [ SettingsLayout.item "Perspective" numbers.mouse_sensitivity
              SettingsLayout.item "Parallel" numbers.parallel_mouse_sensitivity ]
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

        mainTable.Rows.Add(SettingsLayout.full_width (SettingsLayout.spacer 2))

        SettingsLayout.grid
            2
            [ SettingsLayout.item "Mouse X" modes.mouse_x_mode.control
              SettingsLayout.item "Mouse Y" modes.mouse_y_mode.control
              SettingsLayout.item "Middle mouse" modes.middle_mouse_while_flying.control
              SettingsLayout.item "MWheel changes speed" modes.wheel_speed_mode.control
              options.wheel_changes_speed_during_flight_navigation ]
        |> SettingsLayout.full_width
        |> mainTable.Rows.Add

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
                  (binding_editor bindings.cancel_flight_and_restore defaults.cancel_flight_and_restore)
              SettingsLayout.item
                  "Toggle projection"
                  (binding_editor bindings.toggle_projection defaults.toggle_projection) ]
        |> SettingsLayout.full_width
        |> mainTable.Rows.Add

        mainTable.Rows.Add(SettingsLayout.full_width (SettingsLayout.heading "Movement speed and controls"))
        mainTable.Rows.Add(SettingsLayout.full_width (SettingsLayout.subheading "Options"))

        SettingsLayout.grid
            2
            [ SettingsLayout.item "Boost behaviour" modes.boost_mode.control
              SettingsLayout.item "Slow behaviour" modes.slow_mode.control ]
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

        mainTable.Rows.Add(SettingsLayout.full_width (SettingsLayout.heading "Retarget"))

        mainTable.Rows.Add(SettingsLayout.full_width (SettingsLayout.subheading "Automatic retarget"))

        SettingsLayout.grid
            2
            [ SettingsLayout.item "On pivot" modes.retarget_on_pivot.control
              SettingsLayout.item "On pan" modes.retarget_on_pan.control
              SettingsLayout.item "On flight exit" modes.retarget_on_flight_exit.control
              SettingsLayout.item "On restored flight exit" modes.retarget_on_restored_flight_exit.control ]
        |> SettingsLayout.full_width
        |> mainTable.Rows.Add

        mainTable.Rows.Add(SettingsLayout.full_width (SettingsLayout.subheading "Mouse inputs"))

        SettingsLayout.grid
            2
            [ SettingsLayout.item "Mouse 4" modes.mouse4_retarget.control
              SettingsLayout.item "Mouse 5" modes.mouse5_retarget.control
              SettingsLayout.item "Shift + right click" modes.shift_right_click_retarget.control
              SettingsLayout.item "Alt + right click" modes.alt_right_click_retarget.control
              SettingsLayout.item "Ctrl + right click" modes.ctrl_right_click_retarget.control ]
        |> SettingsLayout.full_width
        |> mainTable.Rows.Add

        mainTable.Rows.Add(SettingsLayout.full_width (SettingsLayout.subheading "Direct retarget zoom"))

        SettingsLayout.grid
            2
            [ SettingsLayout.item "Perspective border" numbers.perspective_retarget_zoom_border
              SettingsLayout.item "Parallel border" numbers.parallel_retarget_zoom_border ]
        |> SettingsLayout.full_width
        |> mainTable.Rows.Add

        mainTable.Rows.Add(SettingsLayout.full_width (SettingsLayout.subheading "Fallback distance"))

        SettingsLayout.grid
            2
            [ SettingsLayout.item "Perspective multiplier" numbers.perspective_retarget_fallback_multiplier
              SettingsLayout.item "Parallel multiplier" numbers.parallel_retarget_fallback_multiplier ]
        |> SettingsLayout.full_width
        |> mainTable.Rows.Add

        mainTable.Rows.Add(SettingsLayout.full_width (SettingsLayout.heading "Parallel projection"))

        SettingsLayout.grid
            2
            [ SettingsLayout.item "Parallel flying" modes.parallel_view_flying.control
              SettingsLayout.item "Viewports (comma list)" parallelViewNames
              SettingsLayout.item "Parallel zoom speed" numbers.parallel_zoom_speed_multiplier
              SettingsLayout.item "Parallel up/down multi" numbers.parallel_up_down_multiplier
              SettingsLayout.item "Parallel pivot multi" numbers.parallel_mouse_pivot_multiplier
              SettingsLayout.item "Parallel pan multi" numbers.parallel_mouse_pan_multiplier ]
        |> SettingsLayout.full_width
        |> mainTable.Rows.Add

        mainTable.Rows.Add(SettingsLayout.full_width parallelLensLayout)

        mainTable.Rows.Add(SettingsLayout.full_width (SettingsLayout.heading "Viewport"))

        SettingsLayout.grid
            2
            [ SettingsLayout.item
                  "Forced perspective lens on flight start"
                  numbers.forced_perspective_lens_length_on_flight_start_mm
              SettingsLayout.item
                  "Perspective lens diff during flight"
                  numbers.perspective_lens_length_delta_during_flight_mm ]
        |> SettingsLayout.full_width
        |> mainTable.Rows.Add

        mainTable.Rows.Add(SettingsLayout.full_width (SettingsLayout.heading "Other"))

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

        let host = new TableLayout(Spacing = Size.Empty)
        host.Rows.Add(SettingsLayout.row [ new TableCell(bindingCapture.focus_sink, false) ])

        let contentRow = SettingsLayout.row [ new TableCell(scrollable, true) ]
        contentRow.ScaleHeight <- true
        host.Rows.Add contentRow

        self.Content <- host

        modes.parallel_view_flying.control.SelectedIndexChanged.Add(fun (_: EventArgs) ->
            refresh_parallel_view_controls ())

        [ modes.shift_right_click_action.control
          modes.alt_right_click_action.control
          modes.ctrl_right_click_action.control
          modes.mouse4_action.control
          modes.mouse5_action.control
          modes.shift_right_click_retarget.control
          modes.alt_right_click_retarget.control
          modes.ctrl_right_click_retarget.control
          modes.mouse4_retarget.control
          modes.mouse5_retarget.control
          modes.retarget_on_pivot.control
          modes.retarget_on_pan.control
          modes.retarget_on_flight_exit.control
          modes.retarget_on_restored_flight_exit.control ]
        |> List.iter (fun (control: DropDown) ->
            control.SelectedIndexChanged.Add(fun (_: EventArgs) -> refresh_retarget_controls ()))

        modes.wheel_speed_mode.control.SelectedIndexChanged.Add(fun (_: EventArgs) -> refresh_wheel_speed_controls ())

        refresh_retarget_controls ()
        refresh_wheel_speed_controls ()
        refresh_parallel_view_controls ()

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

    member _.ReadScrollPosition() = scrollable.ScrollPosition

    member _.SetScrollPosition(position: Point) = scrollable.ScrollPosition <- position

    member _.LoadConfig(config: FlyConfigFile) =
        SettingsConfig.load fields.config config
        refresh_retarget_controls ()
        refresh_wheel_speed_controls ()
        refresh_parallel_view_controls ()

    member _.ReadConfig() = SettingsConfig.read fields.config
