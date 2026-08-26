module RhinosCanFly.Options

open Rhino
open Rhino.Commands

let show (document: RhinoDoc) =
    InputDebugTrace.write
        $"Options command show begin document-null={isNull document} flight-running={FlightSession.is_running ()}"

    if FlightSession.is_running () then
        RhinoApp.WriteLine "Exit flight before opening RhinosCanFly Options."
        InputDebugTrace.write "Options command show end result=cancel flight-running=true"
        Result.Cancel
    else
        InputDebugTrace.write "Options command input suspension begin"

        match RuntimeSettings.suspend_input () with
        | Error error ->
            InputDebugTrace.write $"Options command input suspension end result=error error={error}"
            SettingsUi.report_error $"RhinosCanFly could not suspend input for Options: {error}"
            Result.Failure
        | Ok suspension ->
            InputDebugTrace.write
                $"Options command input suspension end result=ok lease={suspension.id} cleanup-error={suspension.cleanup_error}"

            let mutable result = Result.Success

            try
                match suspension.cleanup_error with
                | Some error ->
                    SettingsUi.report_error
                        $"RhinosCanFly Options cannot open because input cleanup is incomplete: {error}"

                    result <- Result.Failure
                | None ->
                    try
                        InputDebugTrace.write "Options dialog construction begin"
                        use dialog = new RhinosCanFlySettingsDialog()
                        InputDebugTrace.write "Options dialog construction end"
                        InputDebugTrace.write "Options dialog ShowForRhino begin"
                        dialog.ShowForRhino document
                        InputDebugTrace.write "Options dialog ShowForRhino end"
                    with error ->
                        InputDebugTrace.write $"Options dialog exception={error}"
                        SettingsUi.report_error $"RhinosCanFly Options failed: {error.Message}"
                        result <- Result.Failure
            finally
                InputDebugTrace.write $"Options command input resume begin lease={suspension.id}"

                match RuntimeSettings.resume_input suspension with
                | Ok() -> InputDebugTrace.write $"Options command input resume end lease={suspension.id} result=ok"
                | Error error ->
                    InputDebugTrace.write
                        $"Options command input resume end lease={suspension.id} result=error error={error}"

                    SettingsUi.report_error $"RhinosCanFly could not resume input after Options: {error}"
                    result <- Result.Failure

            InputDebugTrace.write $"Options command show end result={result}"
            result
