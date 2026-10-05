module RhinosCanFly.FlightLoopHost

let run
    (input_wake: PlatformInputWake.State)
    (input: InputAccumulator.State)
    (raw: PlatformRawInput.Session)
    (state: FlyState)
    =
    let loop =
        FlightLoop.create
            (fun () -> PlatformRawInput.registration_is_current raw)
            (fun () -> PlatformInputWake.acknowledge input_wake)
            input
            state

    NavigationLoop.run
        PlatformInput.wait_for_input_for
        (fun () -> FlyState.is_running state)
        loop.work_pending
        loop.wait_timeout
        loop.step
