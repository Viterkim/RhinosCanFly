module RhinosCanFly.Commands

open System
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

    if RuntimeSettings.input_suspended () then
        RhinoApp.WriteLine "RhinosCanFly is unavailable while an Options dialog is open."
        Result.Cancel
    elif
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
                match FlightSession.run view loaded.config sessionMode with
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
    if FlightSession.is_running () then
        RhinoApp.WriteLine "Exit flight before opening RhinosCanFly Options."
        Result.Cancel
    else
        match RuntimeSettings.suspend_input () with
        | Error error ->
            SettingsUi.report_error $"RhinosCanFly could not suspend input for Options: {error}"
            Result.Failure
        | Ok suspension ->
            let mutable result = Result.Success

            try
                match suspension.cleanup_error with
                | Some error ->
                    SettingsUi.report_error
                        $"RhinosCanFly Options cannot open because input cleanup is incomplete: {error}"

                    result <- Result.Failure
                | None ->
                    try
                        use dialog = new RhinosCanFlySettingsDialog()
                        dialog.ShowForRhino document
                    with error ->
                        SettingsUi.report_error $"RhinosCanFly Options failed: {error.Message}"
                        result <- Result.Failure
            finally
                match RuntimeSettings.resume_input suspension with
                | Ok() -> ()
                | Error error ->
                    SettingsUi.report_error $"RhinosCanFly could not resume input after Options: {error}"
                    result <- Result.Failure

            result

type NavigationCommandMode =
    | PivotCommand
    | PanCommand

let toggle_navigation_command (mode: NavigationCommandMode) (document: RhinoDoc) =
    let name =
        match mode with
        | PivotCommand -> "RhinosCanFlyPivot"
        | PanCommand -> "RhinosCanFlyPan"

    let active =
        match mode with
        | PivotCommand -> PlatformInput.pivot_active ()
        | PanCommand -> PlatformInput.pan_active ()

    if active then
        let stopped =
            match mode with
            | PivotCommand -> PlatformInput.stop_pivot ()
            | PanCommand -> PlatformInput.stop_pan ()

        match stopped with
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
                    let conflictingModeStopped =
                        match mode with
                        | PivotCommand -> PlatformInput.stop_pan ()
                        | PanCommand -> PlatformInput.stop_pivot ()

                    match conflictingModeStopped with
                    | Error error ->
                        RhinoApp.WriteLine $"{name} failed: {error}"
                        Result.Failure
                    | Ok() ->
                        let completion =
                            if
                                DefaultFlightMode.restores_navigation_commands loaded.config_file.default_flight_mode
                            then
                                let viewport = view.ActiveViewport
                                let snapshot = CameraSnapshot.capture viewport
                                let host = PlatformInput.capture_viewport_host view

                                Some(
                                    Action(fun () ->
                                        if PlatformInput.viewport_host_exists host view then
                                            CameraSnapshot.restore viewport snapshot

                                            if PlatformInput.viewport_host_is_foreground host view then
                                                FlightRedraw.redraw loaded.config.behavior.viewport_redraw_mode view)
                                )
                            else
                                None

                        let started =
                            match mode with
                            | PivotCommand -> PlatformInput.start_pivot view completion
                            | PanCommand -> PlatformInput.start_pan view completion

                        match started with
                        | Ok() ->
                            try
                                let behavior = loaded.config.behavior
                                let movement = loaded.config.movement

                                let speed =
                                    FlightSpeed.current
                                        document
                                        behavior.load_speed_from_document
                                        movement.speed_range
                                        movement.base_speed

                                ViewTarget.apply behavior.view_target speed view.ActiveViewport
                                Result.Success
                            with error ->
                                match mode with
                                | PivotCommand -> PlatformInput.stop_pivot () |> ignore
                                | PanCommand -> PlatformInput.stop_pan () |> ignore

                                RhinoApp.WriteLine $"{name} failed to set the view target: {error.Message}"
                                Result.Failure
                        | Error error ->
                            RhinoApp.WriteLine $"{name} failed: {error}"
                            Result.Failure)

let pivot (document: RhinoDoc) =
    if RuntimeSettings.input_suspended () then
        RhinoApp.WriteLine "RhinosCanFlyPivot is unavailable while an Options dialog is open."
        Result.Cancel
    else
        toggle_navigation_command PivotCommand document

let pan (document: RhinoDoc) =
    if RuntimeSettings.input_suspended () then
        RhinoApp.WriteLine "RhinosCanFlyPan is unavailable while an Options dialog is open."
        Result.Cancel
    else
        toggle_navigation_command PanCommand document

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
        FlightSession.recovery_completed ()
        RhinoApp.WriteLine "Input recovery completed."
        Result.Success
    | Error error ->
        RhinoApp.WriteLine $"Input recovery is incomplete: {error} Restart Rhino before flying again."
        Result.Failure

let toggle_target_debug (document: RhinoDoc) =
    let view = document.Views.ActiveView

    if isNull view then
        RhinoApp.WriteLine "RhinosCanFlyTargetDebug: no active view."
        Result.Failure
    elif ViewTargetDebug.enabled () then
        ViewTargetDebug.set_enabled false
        view.Redraw()
        RhinoApp.WriteLine "RhinosCanFly target debug disabled."
        Result.Success
    else
        with_config (fun (loaded: ConfigLoadResult) ->
            let config = loaded.config
            let behavior = config.behavior
            let movement = config.movement

            let speed =
                FlightSpeed.current document behavior.load_speed_from_document movement.speed_range movement.base_speed

            ViewTargetDebug.set_enabled true
            ViewTarget.debug_current behavior.view_target speed view.ActiveViewport
            view.Redraw()

            RhinoApp.WriteLine "Target debug enabled. Run this command again to turn it off."

            Result.Success)
