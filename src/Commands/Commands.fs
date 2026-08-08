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

let run (document: RhinoDoc) =
    let view = document.Views.ActiveView

    if isNull view then
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
                match Runtime.run view loaded.config with
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
