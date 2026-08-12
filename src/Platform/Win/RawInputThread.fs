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
      errors: string list }

type StartFailureException(message: string, restartRequired: bool, innerError: exn) =
    inherit Exception(message, innerError)

    member _.RestartRequired = restartRequired

type Session =
    { thread: Thread
      window_handle: nativeint
      native_thread_id: uint32
      stopped: ManualResetEventSlim
      result: ThreadResult
      registration: RawInputNative.MouseRegistrationLease
      stop_gate: obj
      mutable stop_requested: int
      mutable stopped_disposed: bool
      mutable stop_outcome: StopOutcome option }

type StartupState =
    { ready: ManualResetEventSlim
      stopped: ManualResetEventSlim
      mutable ready_disposed: bool
      mutable window_handle: nativeint
      mutable native_thread_id: uint32
      mutable registration: RawInputNative.MouseRegistrationLease option
      mutable cancel_requested: int
      result: ThreadResult }

type StartupRecovery =
    { thread: Thread
      startup: StartupState
      mutable stopped_disposed: bool }

[<Literal>]
let startup_timeout_ms = 250

[<Literal>]
let stop_observation_ms = 250

[<Literal>]
let join_timeout_ms = 50

let recoveryGate = obj ()
let recoverySessions = ResizeArray<Session>()
let recoveryStartups = ResizeArray<StartupRecovery>()

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

let registration_relinquished (registration: RawInputNative.MouseRegistrationLease option) =
    match registration with
    | Some lease -> lease.relinquished
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
            startup.native_thread_id <- RawInputNative.GetCurrentThreadId()

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

            // Publish before Set so a waiting thread cannot dispose the event
            // while this thread still classifies failures as startup failures.
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
                        Thread.Sleep 250
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
    let signalled = stopped.Wait stop_observation_ms

    if signalled && thread.IsAlive then
        thread.Join join_timeout_ms
    else
        signalled || not thread.IsAlive

let cancel_startup (startup: StartupState) (thread: Thread) =
    Interlocked.Exchange(&startup.cancel_requested, 1) |> ignore

    if startup.window_handle <> nativeint 0 then
        post_stop startup.window_handle |> ignore

    let terminated = observe_termination thread startup.stopped

    if not terminated then
        retain_startup thread startup

    terminated

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
    let result =
        { startup_error = None
          runtime_error = None
          shutdown_error = None }

    let startup =
        { ready = new ManualResetEventSlim(false)
          stopped = new ManualResetEventSlim(false)
          ready_disposed = false
          window_handle = nativeint 0
          native_thread_id = 0u
          registration = None
          cancel_requested = 0
          result = result }

    let thread =
        Thread(ThreadStart(fun () -> run_thread config sessionMode input inputAvailable startup))

    thread.IsBackground <- true
    thread.Name <- "RhinosCanFly raw input"
    thread.SetApartmentState(ApartmentState.STA)
    InputDiagnostics.record InputDiagnostics.EventKind.RawStart 0L 0L
    thread.Start()

    if not (startup.ready.Wait startup_timeout_ms) then
        InputDiagnostics.record InputDiagnostics.EventKind.RawStartFailed 1L 0L
        let terminated = cancel_startup startup thread

        if terminated then
            dispose_startup_events startup

        let message =
            "The raw-input worker did not become ready within 250 ms. Cleanup is continuing on that worker."

        raise (StartFailureException(message, not terminated, TimeoutException message))

    startup.ready.Dispose()
    startup.ready_disposed <- true

    match result.startup_error, startup.registration with
    | Some error, _ ->
        InputDiagnostics.record InputDiagnostics.EventKind.RawStartFailed 2L 0L
        let terminated = cancel_startup startup thread

        if terminated then
            startup.stopped.Dispose()

        let errors =
            exception_messages error
            @ (result.shutdown_error |> Option.map exception_messages |> Option.defaultValue [])

        let message = String.concat "; " errors

        raise (
            StartFailureException(
                message,
                not terminated || not (registration_relinquished startup.registration),
                error
            )
        )
    | None, None ->
        InputDiagnostics.record InputDiagnostics.EventKind.RawStartFailed 3L 0L
        let terminated = cancel_startup startup thread

        if terminated then
            startup.stopped.Dispose()

        let message = "The raw-input worker started without a mouse registration."
        raise (StartFailureException(message, not terminated, InvalidOperationException message))
    | None, Some registration when startup.stopped.IsSet ->
        InputDiagnostics.record InputDiagnostics.EventKind.RawStartFailed 4L 0L
        let terminated = not thread.IsAlive || thread.Join join_timeout_ms

        if terminated then
            startup.stopped.Dispose()
        else
            retain_startup thread startup

        let message = "The raw-input worker stopped before startup completed."

        raise (
            StartFailureException(
                message,
                not terminated || not registration.relinquished,
                InvalidOperationException message
            )
        )
    | None, Some registration ->
        InputDiagnostics.record
            InputDiagnostics.EventKind.RawReady
            (startup.window_handle.ToInt64())
            (int64 startup.native_thread_id)

        { thread = thread
          window_handle = startup.window_handle
          native_thread_id = startup.native_thread_id
          stopped = startup.stopped
          result = result
          registration = registration
          stop_gate = obj ()
          stop_requested = 0
          stopped_disposed = false
          stop_outcome = None }

let request_stop (session: Session) =
    if Interlocked.CompareExchange(&session.stop_requested, 1, 0) = 0 then
        InputDiagnostics.record
            InputDiagnostics.EventKind.RawStopRequested
            (session.window_handle.ToInt64())
            (int64 session.native_thread_id)

    if not session.stopped.IsSet then
        post_stop session.window_handle
    else
        Ok()

let stop_internal (retry: bool) (session: Session) =
    lock session.stop_gate (fun () ->
        match session.stop_outcome with
        | Some outcome when not retry && outcome.terminated && outcome.registration_relinquished -> outcome
        | Some _
        | None ->
            let errors = ResizeArray<string>()

            match request_stop session with
            | Ok() -> ()
            | Error error -> errors.Add error

            let terminated = observe_termination session.thread session.stopped

            if terminated && not session.stopped_disposed then
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
                  errors = List.ofSeq errors }

            InputDiagnostics.record
                (if outcome.terminated && outcome.registration_relinquished then
                     InputDiagnostics.EventKind.RawStopped
                 else
                     InputDiagnostics.EventKind.RawStopTimedOut)
                (if outcome.registration_relinquished then 1L else 0L)
                (int64 outcome.errors.Length)

            session.stop_outcome <- Some outcome

            if outcome.terminated && outcome.registration_relinquished then
                forget_session session
            else
                retain_session session

            outcome)

let stop (session: Session) = stop_internal false session

let runtime_failed (session: Session) =
    Option.isSome session.result.runtime_error

let recovery_count () =
    lock recoveryGate (fun () -> recoverySessions.Count + recoveryStartups.Count)

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

    if terminated then
        if not recovery.startup.ready_disposed then
            recovery.startup.ready.Dispose()
            recovery.startup.ready_disposed <- true

        if not recovery.stopped_disposed then
            recovery.startup.stopped.Dispose()
            recovery.stopped_disposed <- true

        forget_startup recovery

    struct (terminated, List.ofSeq errors)

let retry_recovery () =
    let sessions = lock recoveryGate (fun () -> recoverySessions.ToArray())
    let startups = lock recoveryGate (fun () -> recoveryStartups.ToArray())
    let errors = ResizeArray<string>()

    for session in sessions do
        let outcome = stop_internal true session

        for error in outcome.errors do
            errors.Add error

    for startup in startups do
        let struct (_terminated, startupErrors) = recover_startup startup

        for error in startupErrors do
            errors.Add error

    let remaining =
        lock recoveryGate (fun () -> recoverySessions.Count + recoveryStartups.Count)

    struct (remaining, List.ofSeq errors)
