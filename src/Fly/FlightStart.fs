module RhinosCanFly.FlightStart

open Rhino
open Rhino.Commands

let run (session_mode: FlightSessionMode) (document: RhinoDoc) =
    let view = document.Views.ActiveView

    if RuntimeSettings.input_suspended () then
        RhinoApp.WriteLine "RhinosCanFly is unavailable while an Options dialog is open."
        Result.Cancel
    elif
        session_mode.lifetime = FlightLifetime.WhileRightMouseHeld
        && not (PlatformInput.right_mouse_button_down ())
    then
        Result.Cancel
    elif isNull view then
        RhinoApp.WriteLine "RhinosCanFly: no active view."
        Result.Failure
    else
        CurrentConfig.with_loaded (fun (loaded: ConfigLoadResult) ->
            if not (RuntimeSettings.runtime_enabled ()) then
                RhinoApp.WriteLine "RhinosCanFly is disabled."
                Result.Cancel
            elif not (ViewportNameList.allows view.ActiveViewport.Name loaded.config.viewport_access.capabilities) then
                RhinoApp.WriteLine "RhinosCanFly is disabled for this viewport."
                Result.Cancel
            else
                match FlightSession.run view loaded.config session_mode with
                | Ok() -> Result.Success
                | Error error ->
                    RhinoApp.WriteLine $"RhinosCanFly failed: {error}"
                    Result.Failure)
