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

    if
        sessionMode = FlightSessionMode.WhileRightMouseHeld
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

        let mutable speed =
            FlightSpeed.current
                document
                config.load_speed_from_document
                config.minimum_speed
                config.maximum_speed
                config.base_speed

        let result = RhinoGet.GetNumber("Flying speed", false, &speed)

        if result <> Result.Success then
            result
        else
            match
                FlightSpeed.set document config.save_speed_to_document config.minimum_speed config.maximum_speed speed
            with
            | Ok saved ->
                RhinoApp.WriteLine $"RhinosCanFly speed set to {saved}."
                Result.Success
            | Error error ->
                RhinoApp.WriteLine $"RhinosCanFly: {error}"
                Result.Failure)

let toggle_view_manipulation
    (name: string)
    (isActive: unit -> bool)
    (toggle: nativeint -> Result<unit, string>)
    (document: RhinoDoc)
    =
    if isActive () then
        match toggle (nativeint 0) with
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
                match PlatformInput.get_cursor_position () with
                | Error error ->
                    RhinoApp.WriteLine $"{name} failed: {error}"
                    Result.Failure
                | Ok cursor when not (view.ScreenRectangle.Contains cursor) ->
                    RhinoApp.WriteLine $"{name}: move the cursor over the active viewport."
                    Result.Cancel
                | Ok _ ->
                    match toggle view.Handle with
                    | Ok() -> Result.Success
                    | Error error ->
                        RhinoApp.WriteLine $"{name} failed: {error}"
                        Result.Failure)

let pivot (document: RhinoDoc) =
    toggle_view_manipulation "RhinosCanFlyPivot" PlatformInput.pivot_active PlatformInput.toggle_pivot document

let pan (document: RhinoDoc) =
    toggle_view_manipulation "RhinosCanFlyPan" PlatformInput.pan_active PlatformInput.toggle_pan document
