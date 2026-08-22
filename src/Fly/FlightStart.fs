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
    else
        CurrentConfig.with_loaded (fun (loaded: ConfigLoadResult) ->
            if not (RuntimeSettings.runtime_enabled ()) then
                RhinoApp.WriteLine "RhinosCanFly is disabled."
                Result.Cancel
            elif
                view.ActiveViewport.IsParallelProjection
                && not loaded.config.movement.parallel_view.enabled
            then
                RhinoApp.WriteLine "RhinosCanFly: enable parallel views in Options."
                Result.Cancel
            else
                match FlightSession.run view loaded.config sessionMode with
                | Ok() -> Result.Success
                | Error error ->
                    RhinoApp.WriteLine $"RhinosCanFly failed: {error}"
                    Result.Failure)
