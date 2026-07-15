module RhinosCanFly.Commands

open System
open Rhino
open Rhino.Commands

let run (document: RhinoDoc) =
    let view = document.Views.ActiveView

    if isNull view then
        RhinoApp.WriteLine "RhinosCanFly: no active view."
        Result.Failure
    elif not view.ActiveViewport.IsPerspectiveProjection then
        RhinoApp.WriteLine "RhinosCanFly: use a perspective viewport."
        Result.Cancel
    else
        match Config.load () with
        | Error error ->
            RhinoApp.WriteLine $"RhinosCanFly config error:{Environment.NewLine}{error}"
            Result.Failure
        | Ok loaded ->
            for message in loaded.messages do
                RhinoApp.WriteLine $"RhinosCanFly: {message}."

            match Runtime.run view loaded.config with
            | Ok() -> Result.Success
            | Error error ->
                RhinoApp.WriteLine $"RhinosCanFly failed: {error}"
                Result.Failure
