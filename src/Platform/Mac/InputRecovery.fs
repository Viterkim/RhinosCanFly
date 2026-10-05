module RhinosCanFly.InputRecovery

open Rhino
open Rhino.Commands

let run () =
    try
        FlightSession.stop ExplicitKeepCamera

        match MacNavigationInput.complete_cleanup () with
        | None ->
            FlightSession.recovery_completed ()

            match RuntimeSettings.complete_input_recovery () with
            | Ok() ->
                RhinoApp.WriteLine "Mac navigation stopped. Remaining owned releases are still consumed."
                Result.Success
            | Error error ->
                RhinoApp.WriteLine $"Mac settings recovery failed: {error}"
                Result.Failure
        | Some error ->
            RhinoApp.WriteLine error
            Result.Failure
    with error ->
        RhinoApp.WriteLine $"Mac input recovery failed: {error.Message}"
        Result.Failure
