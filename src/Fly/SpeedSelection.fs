module RhinosCanFly.SpeedSelection

open Rhino
open Rhino.Commands
open Rhino.Input

let run (document: RhinoDoc) =
    CurrentConfig.with_loaded (fun (loaded: ConfigLoadResult) ->
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
