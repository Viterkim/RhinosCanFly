module RhinosCanFly.FlightStart

open Rhino
open Rhino.Commands

let run (sessionMode: FlightSessionMode) (document: RhinoDoc) =
    let view = document.Views.ActiveView

    if RuntimeSettings.input_suspended () then
        RhinoApp.WriteLine "RhinosCanFly is unavailable while an Options dialog is open."
        Result.Cancel
    elif
        sessionMode.lifetime = FlightLifetime.WhileRightMouseHeld
        && not (PlatformInput.right_mouse_button_down ())
    then
        Result.Cancel
    elif isNull view then
        RhinoApp.WriteLine "RhinosCanFly: no active view."
        Result.Failure
    elif not view.ActiveViewport.IsPerspectiveProjection then
        RhinoApp.WriteLine "RhinosCanFly: use a perspective viewport."
        Result.Cancel
    else
        CurrentConfig.with_loaded (fun (loaded: ConfigLoadResult) ->
            if not loaded.config_file.enabled then
                RhinoApp.WriteLine "RhinosCanFly is disabled in Options."
                Result.Cancel
            else
                match FlightSession.run view loaded.config sessionMode with
                | Ok() -> Result.Success
                | Error error ->
                    RhinoApp.WriteLine $"RhinosCanFly failed: {error}"
                    Result.Failure)
