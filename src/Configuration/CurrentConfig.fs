module RhinosCanFly.CurrentConfig

open System
open Rhino
open Rhino.Commands

let with_loaded (run: ConfigLoadResult -> Result) =
    match RuntimeSettings.current () with
    | Error error ->
        RhinoApp.WriteLine $"RhinosCanFly config error:{Environment.NewLine}{error}"
        Result.Failure
    | Ok loaded -> run loaded
