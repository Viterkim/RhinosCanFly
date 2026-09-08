module RhinosCanFly.FlightStart

open System
open Rhino
open Rhino.Commands
open Rhino.Input
open Rhino.Input.Custom

let run_authorized (held_entry: (unit -> bool) option) (session_mode: FlightSessionMode) (document: RhinoDoc) =
    let view = document.Views.ActiveView

    if RuntimeSettings.input_suspended () then
        RhinoApp.WriteLine "RhinosCanFly is unavailable while an Options dialog is open."
        Result.Cancel
    elif isNull view then
        RhinoApp.WriteLine "RhinosCanFly: no active view."
        Result.Failure
    elif
        session_mode.lifetime = FlightLifetime.WhileRightMouseHeld
        && not (held_entry |> Option.exists (fun (valid: unit -> bool) -> valid ()))
    then
        Result.Cancel
    else
        CurrentConfig.with_loaded (fun (loaded: ConfigLoadResult) ->
            if not (RuntimeSettings.runtime_enabled ()) then
                RhinoApp.WriteLine "RhinosCanFly is disabled."
                Result.Cancel
            elif not (ViewportNameList.allows view.ActiveViewport.Name loaded.config.viewport_access.capabilities) then
                RhinoApp.WriteLine "RhinosCanFly is disabled for this viewport."
                Result.Cancel
            else
                match FlightSession.run view loaded.config session_mode held_entry with
                | Ok() -> Result.Success
                | Error error ->
                    RhinoApp.WriteLine $"RhinosCanFly failed: {error}"
                    Result.Failure)

let run (session_mode: FlightSessionMode) (document: RhinoDoc) =
    run_authorized None session_mode document

let run_held (flight_mode: FlightMode) (document: RhinoDoc) (run_mode: RunMode) =
    if run_mode <> RunMode.Scripted then
        Result.Cancel
    else
        let previous_prompt = RhinoApp.CommandPrompt
        use input = new GetString()
        input.SetCommandPrompt "Flight entry"

        let result =
            try
                input.Get()
            finally
                RhinoApp.SetCommandPrompt previous_prompt

        let mutable request_id = Guid.Empty

        if
            result <> GetResult.String
            || not (Guid.TryParse(input.StringResult(), &request_id))
        then
            Result.Cancel
        elif isNull document.Views.ActiveView then
            Result.Cancel
        else
            let permission =
                PlatformMouseActions.consume_held_flight_entry document.Views.ActiveView request_id

            run_authorized permission (FlightSessionMode.while_right_mouse_held flight_mode) document
