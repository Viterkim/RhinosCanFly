module RhinosCanFly.FlightExit

let reason_code (reason: FlightExitReason) =
    match reason with
    | ExplicitKeepCamera -> 1L
    | ExplicitRestoreCamera -> 2L
    | RightMouseReleased -> 3L
    | FocusLost -> 4L
    | HostInvalid -> 5L
    | SessionFailure _ -> 6L

let request (reason: FlightExitReason) (state: FlyState) =
    if FlyState.request_exit reason state then
        PlatformInput.record_flight_exit
            (reason_code reason)
            state.root_window
            (PlatformInput.foreground_root_window ())
