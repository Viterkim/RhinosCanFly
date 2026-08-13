module RhinosCanFly.Commands

open System
open System.IO
open Rhino
open Rhino.Commands
open Rhino.Input

let with_config (run: ConfigLoadResult -> Result) =
    match RuntimeSettings.current () with
    | Error error ->
        RhinoApp.WriteLine $"RhinosCanFly config error:{Environment.NewLine}{error}"
        Result.Failure
    | Ok loaded -> run loaded

let run (sessionMode: FlightSessionMode) (document: RhinoDoc) =
    let view = document.Views.ActiveView

    if
        sessionMode.lifetime = FlightLifetime.WhileRightMouseHeld
        && not (PlatformInput.right_mouse_button_down ())
    then
        Result.Cancel
    elif isNull view then
        RhinoApp.WriteLine "RhinosCanFly: no active view."
        Result.Failure
    elif not view.ActiveViewport.IsPerspectiveProjection then
        RhinoApp.WriteLine "RhinosCanFly: use a perspective viewport."
        Result.Cancel
    else
        with_config (fun (loaded: ConfigLoadResult) ->
            if not loaded.config_file.enabled then
                RhinoApp.WriteLine "RhinosCanFly is disabled in Options."
                Result.Cancel
            else
                match Runtime.run view loaded.config sessionMode with
                | Ok() -> Result.Success
                | Error error ->
                    RhinoApp.WriteLine $"RhinosCanFly failed: {error}"
                    Result.Failure)

let set_speed (document: RhinoDoc) =
    with_config (fun (loaded: ConfigLoadResult) ->
        let config = loaded.config
        let movement = config.movement
        let behavior = config.behavior

        let mutable speed =
            FlightSpeed.current document behavior.load_speed_from_document movement.speed_range movement.base_speed

        let result = RhinoGet.GetNumber("Flying speed", false, &speed)

        if result <> Result.Success then
            result
        else
            match FlightSpeed.set document behavior.save_speed_to_document movement.speed_range speed with
            | Ok saved ->
                RhinoApp.WriteLine $"RhinosCanFly speed set to {saved}."
                Result.Success
            | Error error ->
                RhinoApp.WriteLine $"RhinosCanFly: {error}"
                Result.Failure)

let show_options (document: RhinoDoc) =
    let mutable result = Result.Success
    let dialogStarted = PlatformInput.record_custom_dialog_show_begin ()
    let mutable closedRecorded = false
    let mutable suspension = None

    try
        match RuntimeSettings.suspend_input InputSuspensionReason.CustomOptions with
        | Error error ->
            SettingsUi.report_error $"RhinosCanFly could not suspend input for Options: {error}"
            result <- Result.Failure
        | Ok lease ->
            suspension <- Some lease

            try
                use dialog = new RhinosCanFlySettingsDialog()
                let mutable shownRecorded = false

                dialog.Shown.Add(fun (_: EventArgs) ->
                    if not shownRecorded then
                        shownRecorded <- true
                        PlatformInput.record_custom_dialog_shown dialogStarted)

                dialog.Closed.Add(fun (_: EventArgs) ->
                    if not closedRecorded then
                        closedRecorded <- true
                        PlatformInput.record_custom_dialog_closed dialogStarted)

                dialog.ShowForRhino document

                if dialog.Saved then
                    match RuntimeSettings.commit_staged () with
                    | Ok() -> ()
                    | Error error ->
                        SettingsUi.report_error $"RhinosCanFly settings error: {error}"
                        result <- Result.Failure
                else
                    RuntimeSettings.discard_staged ()
            with error ->
                PlatformInput.record_runtime_exception "RhinosCanFly custom Options command failed" error
                SettingsUi.report_error $"RhinosCanFly Options failed: {error.Message}"
                result <- Result.Failure
    finally
        RuntimeSettings.discard_staged ()

        match suspension with
        | None -> ()
        | Some lease ->
            match RuntimeSettings.resume_input lease with
            | Ok() -> ()
            | Error error ->
                SettingsUi.report_error $"RhinosCanFly could not resume input after Options: {error}"
                result <- Result.Failure

        if not closedRecorded then
            PlatformInput.record_custom_dialog_closed dialogStarted

    result

let toggle_view_manipulation
    (name: string)
    (isActive: unit -> bool)
    (start: Rhino.Display.RhinoView -> Action option -> Result<unit, string>)
    (stop: unit -> Result<unit, string>)
    (stopConflictingMode: unit -> Result<unit, string>)
    (document: RhinoDoc)
    =
    if isActive () then
        match stop () with
        | Ok() -> Result.Success
        | Error error ->
            RhinoApp.WriteLine $"{name} failed: {error}"
            Result.Failure
    else
        with_config (fun (loaded: ConfigLoadResult) ->
            let view = document.Views.ActiveView

            if not loaded.config_file.enabled then
                RhinoApp.WriteLine "RhinosCanFly is disabled in Options."
                Result.Cancel
            elif isNull view then
                RhinoApp.WriteLine $"{name}: no active view."
                Result.Failure
            else
                match PlatformInput.cursor_is_over_view view with
                | Error error ->
                    RhinoApp.WriteLine $"{name} failed: {error}"
                    Result.Failure
                | Ok false ->
                    RhinoApp.WriteLine $"{name}: move the cursor over the active viewport."
                    Result.Cancel
                | Ok true ->
                    match stopConflictingMode () with
                    | Error error ->
                        RhinoApp.WriteLine $"{name} failed: {error}"
                        Result.Failure
                    | Ok() ->
                        let completion =
                            if DefaultFlightMode.restores_solo_commands loaded.config_file.default_flight_mode then
                                let viewport = view.ActiveViewport
                                let snapshot = CameraSnapshot.capture viewport
                                let host = PlatformInput.capture_flight_host view

                                Some(
                                    Action(fun () ->
                                        if PlatformInput.flight_host_is_foreground host view then
                                            CameraSnapshot.restore viewport snapshot
                                            FlightRedraw.redraw loaded.config.behavior.viewport_redraw_mode view)
                                )
                            else
                                None

                        match start view completion with
                        | Ok() -> Result.Success
                        | Error error ->
                            RhinoApp.WriteLine $"{name} failed: {error}"
                            Result.Failure)

let pivot (document: RhinoDoc) =
    toggle_view_manipulation
        "RhinosCanFlyPivot"
        PlatformInput.pivot_active
        PlatformInput.start_pivot
        PlatformInput.stop_pivot
        PlatformInput.stop_pan
        document

let pan (document: RhinoDoc) =
    toggle_view_manipulation
        "RhinosCanFlyPan"
        PlatformInput.pan_active
        PlatformInput.start_pan
        PlatformInput.stop_pan
        PlatformInput.stop_pivot
        document

let show_input_diagnostics () =
    let lines = ResizeArray<string>()
    lines.Add "RhinosCanFly input diagnostics"
    lines.Add $"Captured: {DateTimeOffset.Now:O}"
    lines.Add $"Flight state: {Runtime.state_name ()}"
    lines.Add $"Raw cleanup items: {PlatformInput.raw_input_recovery_count ()}"
    lines.Add $"Cursor clip cleanup items: {PlatformInput.cursor_clip_recovery_count ()}"
    lines.Add(RightClickEntry.diagnostic_line ())

    for line in RuntimeSettings.diagnostic_lines () do
        lines.Add line

    for line in PlatformInput.input_state_diagnostic_lines () do
        lines.Add line

    for line in PlatformInput.input_diagnostic_lines () do
        lines.Add line

    try
        let diagnosticsPath = ConfigStorage.input_diagnostics_path ()
        File.WriteAllLines(diagnosticsPath, lines)

        for line in lines do
            SettingsUi.report_error line

        SettingsUi.report_error $"Saved to: {diagnosticsPath}"
        Result.Success
    with error ->
        SettingsUi.report_error $"RhinosCanFly could not save input diagnostics: {error.Message}"
        Result.Failure

let recover_input () =
    let struct (remainingRawSessions, rawErrors) =
        PlatformInput.retry_raw_input_cleanup ()

    let hookErrors = PlatformInput.retry_input_hook_cleanup ()

    let struct (remainingCursorClips, cursorErrors) =
        PlatformInput.retry_cursor_clip_cleanup ()

    RhinoApp.WriteLine "RhinosCanFly input recovery"
    RhinoApp.WriteLine $"Raw cleanup items remaining: {remainingRawSessions}"
    RhinoApp.WriteLine $"Cursor clips remaining: {remainingCursorClips}"

    for error in rawErrors do
        RhinoApp.WriteLine $"Raw cleanup: {error}"

    for error in hookErrors do
        RhinoApp.WriteLine $"Hook cleanup: {error}"

    for error in cursorErrors do
        RhinoApp.WriteLine $"Cursor clip cleanup: {error}"

    let nativeRecoveryComplete =
        remainingRawSessions = 0 && remainingCursorClips = 0 && List.isEmpty hookErrors

    let settingsRecovery =
        if nativeRecoveryComplete then
            RuntimeSettings.complete_input_recovery ()
        else
            Error "Native input cleanup is incomplete."

    match settingsRecovery with
    | Ok() ->
        Runtime.recovery_completed ()
        RhinoApp.WriteLine "Input recovery completed."
        Result.Success
    | Error error ->
        RhinoApp.WriteLine $"Input recovery is incomplete: {error} Restart Rhino before flying again."
        Result.Failure
