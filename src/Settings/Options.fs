module RhinosCanFly.Options

open Rhino
open Rhino.Commands

let show (document: RhinoDoc) =
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
