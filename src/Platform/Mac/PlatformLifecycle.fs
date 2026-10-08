module RhinosCanFly.PlatformLifecycle

let prepare () : Result<unit, string> =
    FlightSession.prepare ()
    PlatformMouseActions.prepare MacMouseDispatch.dispatch FlightSession.is_running
    Ok()

let shutdown (report: string -> unit) =
    try
        MacNavigationInput.request_stop HostInvalid
        MacNavigationInput.complete_cleanup () |> Option.iter report
    with error ->
        report $"RhinosCanFly Mac input shutdown failed: {error.Message}"

    PlatformMouseActions.shutdown ()
