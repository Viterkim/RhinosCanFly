module RhinosCanFly.InputRecovery

open Rhino
open Rhino.Commands

let run () =
    let struct (remaining_raw_sessions, raw_errors) = PlatformRawInput.retry_recovery ()

    let struct (remaining_cursor_clips, cursor_errors) =
        PlatformCursorClip.retry_cleanup ()

    let hook_errors = PlatformMouseActions.retry_hook_cleanup ()

    RhinoApp.WriteLine "RhinosCanFly input recovery"
    RhinoApp.WriteLine $"Raw cleanup items remaining: {remaining_raw_sessions}"
    RhinoApp.WriteLine $"Cursor clips remaining: {remaining_cursor_clips}"

    for error in raw_errors do
        RhinoApp.WriteLine $"Raw cleanup: {error}"

    for error in hook_errors do
        RhinoApp.WriteLine $"Hook cleanup: {error}"

    for error in cursor_errors do
        RhinoApp.WriteLine $"Cursor clip cleanup: {error}"

    let native_recovery_complete =
        remaining_raw_sessions = 0
        && remaining_cursor_clips = 0
        && List.isEmpty hook_errors

    let settings_recovery =
        if native_recovery_complete then
            RuntimeSettings.complete_input_recovery ()
        else
            Error "Native input cleanup is incomplete."

    match settings_recovery with
    | Ok() ->
        FlightSession.recovery_completed ()
        RhinoApp.WriteLine "Input recovery completed."
        Result.Success
    | Error error ->
        RhinoApp.WriteLine $"Input recovery is incomplete: {error} Restart Rhino before flying again."
        Result.Failure
