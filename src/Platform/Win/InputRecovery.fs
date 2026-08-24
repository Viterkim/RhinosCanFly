module RhinosCanFly.InputRecovery

open Rhino
open Rhino.Commands

let run () =
    let struct (remainingRawSessions, rawErrors) =
        PlatformInput.retry_raw_input_cleanup ()

    let struct (remainingCursorClips, cursorErrors) =
        PlatformInput.retry_cursor_clip_cleanup ()

    let hookErrors = PlatformInput.retry_input_hook_cleanup ()

    RhinoApp.WriteLine "RhinosCanFly input recovery"
    RhinoApp.WriteLine $"Raw cleanup items remaining: {remainingRawSessions}"
    RhinoApp.WriteLine $"Cursor clips remaining: {remainingCursorClips}"

    for error in rawErrors do
        RhinoApp.WriteLine $"Raw cleanup: {error}"

    for error in hookErrors do
        RhinoApp.WriteLine $"Hook cleanup: {error}"

    for error in cursorErrors do
        RhinoApp.WriteLine $"Cursor clip cleanup: {error}"

    let nativeRecoveryComplete =
        remainingRawSessions = 0 && remainingCursorClips = 0 && List.isEmpty hookErrors

    let settingsRecovery =
        if nativeRecoveryComplete then
            RuntimeSettings.complete_input_recovery ()
        else
            Error "Native input cleanup is incomplete."

    match settingsRecovery with
    | Ok() ->
        FlightSession.recovery_completed ()
        RhinoApp.WriteLine "Input recovery completed."
        Result.Success
    | Error error ->
        RhinoApp.WriteLine $"Input recovery is incomplete: {error} Restart Rhino before flying again."
        Result.Failure
