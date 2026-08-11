module RhinosCanFly.Commands

open System
open System.Diagnostics
open Rhino
open Rhino.Commands
open Rhino.Input

let options_capture_release_timeout = TimeSpan.FromSeconds 2.0
let options_capture_poll_interval = TimeSpan.FromMilliseconds 20.0

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
    match PlatformInput.suspend_mouse_button_overrides () with
    | Error error ->
        RhinoApp.WriteLine
            $"RhinosCanFly Options failed: {error} Use Tools > Options > Rhinos Can Fly to change settings."

        Result.Failure
    | Ok() ->
        let mutable result = Result.Success

        try
            try
                let view = document.Views.ActiveView

                if not (isNull view) then
                    let releaseClock = Stopwatch.StartNew()

                    while Runtime.viewport_gesture_active view
                          && releaseClock.Elapsed < options_capture_release_timeout do
                        PlatformInput.wait_for_input_for options_capture_poll_interval
                        RhinoApp.Wait()

                    if Runtime.viewport_gesture_active view then
                        failwith "The active viewport did not release its mouse capture."

                use dialog = new RhinosCanFlySettingsDialog()
                dialog.ShowForRhino document
            with error ->
                RhinoApp.WriteLine $"RhinosCanFly Options failed: {error.Message}"
                result <- Result.Failure
        finally
            match PlatformInput.resume_mouse_button_overrides () with
            | Ok() -> ()
            | Error error ->
                RhinoApp.WriteLine $"RhinosCanFly mouse button overrides could not resume: {error}"
                result <- Result.Failure

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

                                Some(
                                    Action(fun () ->
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
