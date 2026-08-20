module RhinosCanFly.Commands.RhinosCanFlyToggleEnable

open global.RhinosCanFly
open Rhino
open Rhino.Commands

let run (_document: RhinoDoc) =
    match RuntimeSettings.toggle_runtime_enabled () with
    | Ok true ->
        RhinoApp.WriteLine "RhinosCanFly enabled for this Rhino session."
        Result.Success
    | Ok false ->
        RhinoApp.WriteLine "RhinosCanFly disabled for this Rhino session."
        Result.Success
    | Error error ->
        RhinoApp.WriteLine $"RhinosCanFlyToggleEnable failed: {error}"
        Result.Failure
