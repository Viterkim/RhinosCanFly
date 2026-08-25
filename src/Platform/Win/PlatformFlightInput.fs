module RhinosCanFly.PlatformFlightInput

open System

let right_release_passthrough = Action PlatformFlightKeyboard.allow_passthrough

let start (config: FlyConfig) (input: InputAccumulator.State) (inputAvailable: Action) (rightMouseReleaseExits: bool) =
    match PlatformFlightKeyboard.start config input inputAvailable with
    | Error error -> Error error
    | Ok() when not rightMouseReleaseExits -> Ok()
    | Ok() ->
        match PlatformMouseActions.configure_flight_right_release_observer (Some right_release_passthrough) with
        | Ok() -> Ok()
        | Error error ->
            PlatformFlightKeyboard.stop ()
            Error error

let stop () =
    PlatformFlightKeyboard.stop ()
    PlatformMouseActions.configure_flight_right_release_observer None |> ignore
