module RhinosCanFly.Platform.Win.RawInputThread

open System
open System.Diagnostics
open System.Threading
open System.Windows.Forms
open RhinosCanFly

type ThreadResult =
    { mutable startup_error: exn option
      mutable runtime_error: exn option
      mutable shutdown_error: exn option }

type StopOutcome =
    { terminated: bool
      registration_relinquished: bool
      previous_registration_lost: bool
      errors: string list }

type StopAttempt =
    | InitialStop
    | RecoveryRetry

type StartFailureException(message: string, restartRequired: bool, innerError: exn) =
    inherit Exception(message, innerError)

    member _.RestartRequired = restartRequired

type Session =
    { thread: Thread
      window_handle: nativeint
      stopped: ManualResetEventSlim
      result: ThreadResult
      registration: RawInputNative.MouseRegistrationLease
      stop_gate: obj
      mutable stopped_disposed: bool
      mutable stop_outcome: StopOutcome option }

type StartupState =
    { ready: ManualResetEventSlim
      stopped: ManualResetEventSlim
      mutable ready_disposed: bool
      mutable window_handle: nativeint
      mutable registration: RawInputNative.MouseRegistrationLease option
      mutable cancel_requested: int
      result: ThreadResult }

type StartupRecovery =
    { thread: Thread
      startup: StartupState
      mutable stopped_disposed: bool }

[<Literal>]
let STARTUP_TIMEOUT_MS = 250

[<Literal>]
let STOP_OBSERVATION_MS = 1000

[<Literal>]
let JOIN_TIMEOUT_MS = 100

let recoveryGate = obj ()
let recoverySessions = ResizeArray<Session>()
let recoveryStartups = ResizeArray<StartupRecovery>()

let recovery_pending () =
    lock recoveryGate (fun () -> recoverySessions.Count > 0 || recoveryStartups.Count > 0)

let retain_session (session: Session) =
    lock recoveryGate (fun () ->
        let exists =
            recoverySessions
            |> Seq.exists (fun (candidate: Session) -> Object.ReferenceEquals(candidate, session))

        if not exists then
            recoverySessions.Add session)

let forget_session (session: Session) =
    lock recoveryGate (fun () ->
        let mutable index = recoverySessions.Count - 1

        while index >= 0 do
            if Object.ReferenceEquals(recoverySessions[index], session) then
                recoverySessions.RemoveAt index

            index <- index - 1)

let retain_startup (thread: Thread) (startup: StartupState) =
    lock recoveryGate (fun () ->
        let exists =
            recoveryStartups
            |> Seq.exists (fun (candidate: StartupRecovery) -> Object.ReferenceEquals(candidate.thread, thread))

        if not exists then
            recoveryStartups.Add
                { thread = thread
                  startup = startup
                  stopped_disposed = false })

let forget_startup (recovery: StartupRecovery) =
    lock recoveryGate (fun () ->
        let mutable index = recoveryStartups.Count - 1

        while index >= 0 do
            if Object.ReferenceEquals(recoveryStartups[index], recovery) then
                recoveryStartups.RemoveAt index

            index <- index - 1)

let exception_messages (error: exn) =
    match error with
    | :? AggregateException as aggregate ->
        aggregate.Flatten().InnerExceptions
        |> Seq.map (fun (inner: exn) -> inner.Message)
        |> List.ofSeq
    | _ -> [ error.Message ]

let try_complete_registration_cleanup (registration: RawInputNative.MouseRegistrationLease option) =
    match registration with
    | Some lease ->
        match RawInputNative.release_mouse_registration lease with
        | Ok RawInputNative.OwnRegistrationRemovedButPreviousRegistrationLost
        | Error _ -> false
        | Ok RawInputNative.RestoredPrevious
        | Ok RawInputNative.AlreadyRelinquished
        | Ok RawInputNative.ReplacedByAnotherOwner -> true
    | None -> true

let post_stop (window: nativeint) =
    if window = nativeint 0 then
        Error "The raw-input window is not ready yet."
    else
        RawInputNative.post_stop window

let run_thread
    (config: RawInputConfig)
    (sessionMode: FlightSessionMode)
    (input: InputAccumulator.State)
    (inputAvailable: Action)
    (startup: StartupState)
    =
    let mutable receiver: RawInputReceiver option = None
    let mutable readyPublished = false

    try
        try
            let registrationReady =
                Action<RawInputNative.MouseRegistrationLease>(fun (lease: RawInputNative.MouseRegistrationLease) ->
                    startup.registration <- Some lease)

            let runtimeFailed =
                Action<exn>(fun (error: exn) ->
                    if Option.isNone startup.result.runtime_error then
                        startup.result.runtime_error <- Some error

                    InputAccumulator.request_exit (SessionFailure(error.ToString())) input
                    inputAvailable.Invoke())

            let created =
                new RawInputReceiver(config, sessionMode, input, inputAvailable, registrationReady, runtimeFailed)

            receiver <- Some created
            startup.window_handle <- created.WindowHandle

            match created.StartupError with
            | Some error -> startup.result.startup_error <- Some error
            | None -> ()

            // Publish before Set so the waiter cannot dispose the event while startup can still fail.
            readyPublished <- true
            startup.ready.Set()

            if
                Volatile.Read(&startup.cancel_requested) <> 0
                || Option.isSome created.StartupError
            then
                created.RequestStop()

            if not created.RegistrationRelinquished then
                Application.Run()

            while not created.RegistrationRelinquished do
                created.RequestStop()
                Application.Run()
        with error ->
            if not readyPublished then
                startup.result.startup_error <- Some error
                readyPublished <- true
                startup.ready.Set()
            else
                if Option.isNone startup.result.runtime_error then
                    Debug.WriteLine $"RhinosCanFly raw-input thread failed: {error.Message}"
                    startup.result.runtime_error <- Some error

                InputAccumulator.request_exit (SessionFailure(error.ToString())) input
                inputAvailable.Invoke()

            match receiver with
            | Some created ->
                while not created.RegistrationRelinquished do
                    created.RequestStop()

                    if not created.RegistrationRelinquished then
                        Thread.Sleep RawInputNative.REGISTRATION_RETRY_INTERVAL_MS
            | None -> ()
    finally
        try
            match receiver with
            | Some created -> created.ReleaseResources()
            | None -> ()
        with error ->
            startup.result.shutdown_error <- Some error

        startup.stopped.Set()

let observe_termination (thread: Thread) (stopped: ManualResetEventSlim) =
    let signalled = stopped.Wait STOP_OBSERVATION_MS

    if signalled && thread.IsAlive then
        thread.Join JOIN_TIMEOUT_MS
    else
        signalled || not thread.IsAlive

let cancel_startup (startup: StartupState) (thread: Thread) =
    Interlocked.Exchange(&startup.cancel_requested, 1) |> ignore

    if startup.window_handle <> nativeint 0 then
        post_stop startup.window_handle |> ignore

    let terminated = observe_termination thread startup.stopped

    let cleanupComplete =
        terminated && try_complete_registration_cleanup startup.registration

    if not cleanupComplete then
        retain_startup thread startup

    cleanupComplete

let dispose_startup_events (startup: StartupState) =
    if not startup.ready_disposed then
        startup.ready.Dispose()
        startup.ready_disposed <- true

    startup.stopped.Dispose()

let start
    (config: RawInputConfig)
    (sessionMode: FlightSessionMode)
    (input: InputAccumulator.State)
    (inputAvailable: Action)
    =
    if recovery_pending () then
        let message =
            "A previous raw-input worker still needs cleanup. Run RhinosCanFlyInputRecover or restart Rhino."

        raise (StartFailureException(message, true, InvalidOperationException message))

    let result =
        { startup_error = None
          runtime_error = None
          shutdown_error = None }

    let startup =
        { ready = new ManualResetEventSlim(false)
          stopped = new ManualResetEventSlim(false)
          ready_disposed = false
          window_handle = nativeint 0
          registration = None
          cancel_requested = 0
          result = result }

    let thread =
        Thread(ThreadStart(fun () -> run_thread config sessionMode input inputAvailable startup))

    thread.IsBackground <- true
    thread.Name <- "RhinosCanFly raw input"
    thread.SetApartmentState(ApartmentState.STA)
    thread.Start()

    if not (startup.ready.Wait STARTUP_TIMEOUT_MS) then
        let cleanupComplete = cancel_startup startup thread

        if cleanupComplete then
            dispose_startup_events startup

        let message =
            "The raw-input worker did not become ready within 250 ms. Cleanup is continuing on that worker."

        raise (StartFailureException(message, not cleanupComplete, TimeoutException message))

    startup.ready.Dispose()
    startup.ready_disposed <- true

    match result.startup_error, startup.registration with
    | Some error, _ ->
        let cleanupComplete = cancel_startup startup thread

        if cleanupComplete then
            startup.stopped.Dispose()

        let errors =
            exception_messages error
            @ (result.shutdown_error |> Option.map exception_messages |> Option.defaultValue [])

        let message = String.concat "; " errors

        raise (StartFailureException(message, not cleanupComplete, error))
    | None, None ->
        let cleanupComplete = cancel_startup startup thread

        if cleanupComplete then
            startup.stopped.Dispose()

        let message = "The raw-input worker started without a mouse registration."
        raise (StartFailureException(message, not cleanupComplete, InvalidOperationException message))
    | None, Some registration when startup.stopped.IsSet ->
        let terminated = not thread.IsAlive || thread.Join JOIN_TIMEOUT_MS

        let cleanupComplete =
            terminated && try_complete_registration_cleanup startup.registration

        if cleanupComplete then
            startup.stopped.Dispose()
        else
            retain_startup thread startup

        let message = "The raw-input worker stopped before startup completed."

        raise (StartFailureException(message, not cleanupComplete, InvalidOperationException message))
    | None, Some registration ->
        { thread = thread
          window_handle = startup.window_handle
          stopped = startup.stopped
          result = result
          registration = registration
          stop_gate = obj ()
          stopped_disposed = false
          stop_outcome = None }

let request_stop (session: Session) =
    if not session.stopped.IsSet then
        post_stop session.window_handle
    else
        Ok()

let stop_internal (attempt: StopAttempt) (session: Session) =
    lock session.stop_gate (fun () ->
        match session.stop_outcome with
        | Some outcome when attempt = InitialStop -> outcome
        | Some _
        | None ->
            let errors = ResizeArray<string>()

            match request_stop session with
            | Ok() -> ()
            | Error error -> errors.Add error

            let terminated = observe_termination session.thread session.stopped

            let cleanupComplete =
                terminated && try_complete_registration_cleanup (Some session.registration)

            let previousRegistrationLost = session.registration.previous_registration_lost

            if cleanupComplete && not session.stopped_disposed then
                session.stopped.Dispose()
                session.stopped_disposed <- true

            match session.result.runtime_error with
            | Some error -> exception_messages error |> Seq.iter errors.Add
            | None -> ()

            match session.result.shutdown_error with
            | Some error -> exception_messages error |> Seq.iter errors.Add
            | None -> ()

            if not terminated then
                errors.Add "The raw-input worker is still cleaning up in the background."

            let outcome =
                { terminated = terminated
                  registration_relinquished = session.registration.relinquished
                  previous_registration_lost = previousRegistrationLost
                  errors = List.ofSeq errors }

            session.stop_outcome <- Some outcome

            if
                outcome.terminated
                && outcome.registration_relinquished
                && not outcome.previous_registration_lost
            then
                forget_session session
            else
                retain_session session

            outcome)

let stop (session: Session) = stop_internal InitialStop session

let runtime_failed (session: Session) =
    Option.isSome session.result.runtime_error

let registration_is_current (session: Session) =
    RawInputNative.mouse_registration_is_current session.registration

let recover_startup (recovery: StartupRecovery) =
    let errors = ResizeArray<string>()
    Interlocked.Exchange(&recovery.startup.cancel_requested, 1) |> ignore

    if
        recovery.startup.window_handle <> nativeint 0
        && not recovery.startup.stopped.IsSet
    then
        match post_stop recovery.startup.window_handle with
        | Ok() -> ()
        | Error error -> errors.Add error

    let terminated = observe_termination recovery.thread recovery.startup.stopped

    let cleanupComplete =
        terminated && try_complete_registration_cleanup recovery.startup.registration

    if cleanupComplete then
        if not recovery.startup.ready_disposed then
            recovery.startup.ready.Dispose()
            recovery.startup.ready_disposed <- true

        if not recovery.stopped_disposed then
            recovery.startup.stopped.Dispose()
            recovery.stopped_disposed <- true

        forget_startup recovery

    match recovery.startup.registration with
    | Some lease when lease.previous_registration_lost ->
        errors.Add "The previous raw-mouse registration was lost. Restart Rhino before flying again."
    | Some _
    | None -> ()

    struct (cleanupComplete, List.ofSeq errors)

let retry_recovery () =
    let sessions = lock recoveryGate (fun () -> recoverySessions.ToArray())
    let startups = lock recoveryGate (fun () -> recoveryStartups.ToArray())
    let errors = ResizeArray<string>()

    for session in sessions do
        let outcome = stop_internal RecoveryRetry session

        for error in outcome.errors do
            errors.Add error

    for startup in startups do
        let struct (_terminated, startupErrors) = recover_startup startup

        for error in startupErrors do
            errors.Add error

    let remaining =
        lock recoveryGate (fun () -> recoverySessions.Count + recoveryStartups.Count)

    struct (remaining, List.ofSeq errors)
