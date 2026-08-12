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
      mutable stop_signalled: int
      mutable stop_signal_errors: string list
      mutable stopped_disposed: bool
      mutable stop_outcome: StopOutcome option }

type StartupState =
    { ready: ManualResetEventSlim
      stopped: ManualResetEventSlim
      mutable window_handle: nativeint
      mutable native_thread_id: uint32
      mutable registration: RawInputNative.MouseRegistrationLease option
      mutable cancel_requested: int
      result: ThreadResult }

[<Literal>]
let startup_timeout_ms = 5000

[<Literal>]
let shutdown_timeout_ms = 1000

[<Literal>]
let shutdown_escalation_timeout_ms = 1000

[<Literal>]
let join_timeout_ms = 250

let recoveryGate = obj ()
let recoverySessions = ResizeArray<Session>()
let recoveryRegistrations = ResizeArray<RawInputNative.MouseRegistrationLease>()

let retain_for_recovery (session: Session) =
    lock recoveryGate (fun () ->
        let alreadyRetained =
            recoverySessions
            |> Seq.exists (fun (candidate: Session) -> Object.ReferenceEquals(candidate, session))

        if not alreadyRetained then
            recoverySessions.Add session)

let forget_recovery (session: Session) =
    lock recoveryGate (fun () ->
        let mutable index = recoverySessions.Count - 1

        while index >= 0 do
            if Object.ReferenceEquals(recoverySessions[index], session) then
                recoverySessions.RemoveAt index

            index <- index - 1)

let retain_registration_recovery (lease: RawInputNative.MouseRegistrationLease) =
    lock recoveryGate (fun () ->
        let alreadyRetained =
            recoveryRegistrations
            |> Seq.exists (fun (candidate: RawInputNative.MouseRegistrationLease) ->
                Object.ReferenceEquals(candidate, lease))

        if not alreadyRetained then
            recoveryRegistrations.Add lease)

let forget_registration_recovery (lease: RawInputNative.MouseRegistrationLease) =
    lock recoveryGate (fun () ->
        let mutable index = recoveryRegistrations.Count - 1

        while index >= 0 do
            if Object.ReferenceEquals(recoveryRegistrations[index], lease) then
                recoveryRegistrations.RemoveAt index

            index <- index - 1)

let exception_messages (error: exn) =
    match error with
    | :? AggregateException as aggregate ->
        aggregate.Flatten().InnerExceptions
        |> Seq.map (fun (inner: exn) -> inner.Message)
        |> List.ofSeq
    | _ -> [ error.Message ]

let release_registration (lease: RawInputNative.MouseRegistrationLease) =
    match RawInputNative.release_mouse_registration lease with
    | Ok _ -> []
    | Error error -> [ error ]

let run_thread
    (config: RawInputConfig)
    (sessionMode: FlightSessionMode)
    (input: InputAccumulator.State)
    (inputAvailable: Action)
    (startup: StartupState)
    =
    let mutable receiver: RawInputReceiver option = None
    let mutable startupComplete = false

    try
        try
            startup.native_thread_id <- RawInputNative.GetCurrentThreadId()

            let registrationReady =
                Action<RawInputNative.MouseRegistrationLease>(fun (lease: RawInputNative.MouseRegistrationLease) ->
                    startup.registration <- Some lease)

            let created =
                new RawInputReceiver(config, sessionMode, input, inputAvailable, registrationReady)

            receiver <- Some created
            startup.window_handle <- created.WindowHandle

            if Volatile.Read(&startup.cancel_requested) = 0 then
                startupComplete <- true
                startup.ready.Set()
                Application.Run()
        with error ->
            if startupComplete then
                Debug.WriteLine $"RhinosCanFly raw-input thread failed: {error.Message}"
                startup.result.runtime_error <- Some error
                InputAccumulator.request_exit input
                inputAvailable.Invoke()
            else
                startup.result.startup_error <- Some error
                startup.ready.Set()
    finally
        try
            try
                match receiver with
                | Some created -> created.ReleaseResources()
                | None ->
                    match startup.registration with
                    | Some lease ->
                        match RawInputNative.release_mouse_registration lease with
                        | Ok _ -> ()
                        | Error error -> raise (InvalidOperationException error)
                    | None -> ()
            with error ->
                startup.result.shutdown_error <- Some error

                match startup.registration with
                | Some lease when not lease.relinquished -> retain_registration_recovery lease
                | Some _
                | None -> ()
        finally
            startup.stopped.Set()

let abandon_startup (startup: StartupState) (thread: Thread) =
    let errors = ResizeArray<string>()
    Interlocked.Exchange(&startup.cancel_requested, 1) |> ignore

    match startup.registration with
    | Some lease -> release_registration lease |> Seq.iter errors.Add
    | None -> ()

    let registrationRelinquished =
        match startup.registration with
        | Some lease -> lease.relinquished
        | None -> true

    if registrationRelinquished && startup.window_handle <> nativeint 0 then
        match RawInputNative.post_stop startup.window_handle with
        | Ok() -> ()
        | Error error -> errors.Add error

    if registrationRelinquished && startup.native_thread_id <> 0u then
        match RawInputNative.post_quit startup.native_thread_id with
        | Ok() -> ()
        | Error error -> errors.Add error

    let stopped = startup.stopped.Wait shutdown_escalation_timeout_ms

    let terminated =
        if stopped then
            if thread.Join join_timeout_ms then
                true
            else
                errors.Add "The raw-input thread signalled completion but did not terminate."
                false
        else
            false

    struct (terminated, List.ofSeq errors)

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
        let struct (terminated, cleanupErrors) = abandon_startup startup thread

        if terminated then
            startup.ready.Dispose()
            startup.stopped.Dispose()

        let cleanupMessage = String.concat "; " cleanupErrors

        let suffix =
            if String.IsNullOrEmpty cleanupMessage then
                ""
            else
                $" Cleanup: {cleanupMessage}"

        let registrationRelinquished =
            match startup.registration with
            | None -> true
            | Some lease -> lease.relinquished

        let message = $"The raw-input thread did not start within five seconds.{suffix}"
        raise (StartFailureException(message, not terminated || not registrationRelinquished, TimeoutException message))

    startup.ready.Dispose()

    match result.startup_error, startup.registration with
    | Some error, _ ->
        InputDiagnostics.record InputDiagnostics.EventKind.RawStartFailed 2L 0L
        let mutable terminated = startup.stopped.Wait shutdown_timeout_ms

        let cleanupErrors =
            if terminated then
                if not (thread.Join join_timeout_ms) then
                    terminated <- false
                    [ "The failed raw-input thread did not terminate." ]
                else
                    []
            else
                let struct (stoppedAfterEscalation, errors) = abandon_startup startup thread
                terminated <- stoppedAfterEscalation
                errors

        if terminated then
            startup.stopped.Dispose()

        let errors = exception_messages error @ cleanupErrors
        let message = String.concat "; " errors

        let registrationRelinquished =
            match startup.registration with
            | None -> true
            | Some lease -> lease.relinquished

        raise (StartFailureException(message, not terminated || not registrationRelinquished, error))
    | None, None ->
        InputDiagnostics.record InputDiagnostics.EventKind.RawStartFailed 3L 0L
        let struct (terminated, cleanupErrors) = abandon_startup startup thread

        if terminated then
            startup.stopped.Dispose()

        let cleanupMessage = String.concat "; " cleanupErrors

        let suffix =
            if String.IsNullOrEmpty cleanupMessage then
                ""
            else
                $" Cleanup: {cleanupMessage}"

        let message =
            $"The raw-input thread started without owning a mouse registration.{suffix}"

        raise (StartFailureException(message, not terminated, InvalidOperationException message))
    | None, Some registration when startup.stopped.IsSet ->
        InputDiagnostics.record InputDiagnostics.EventKind.RawStartFailed 4L 0L
        let terminated = thread.Join join_timeout_ms

        if terminated then
            startup.stopped.Dispose()

        let message = "The raw-input thread stopped before startup completed."

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
          stop_signalled = 0
          stop_signal_errors = []
          stopped_disposed = false
          stop_outcome = None }

let signal_stop (session: Session) =
    if
        session.registration.relinquished
        && Interlocked.CompareExchange(&session.stop_signalled, 1, 0) = 0
    then
        let errors = ResizeArray<string>()

        if not session.stopped.IsSet then
            match RawInputNative.post_stop session.window_handle with
            | Ok() -> ()
            | Error windowError ->
                errors.Add windowError

                match RawInputNative.post_quit session.native_thread_id with
                | Ok() -> ()
                | Error threadError -> errors.Add threadError

        session.stop_signal_errors <- List.ofSeq errors

let request_stop (session: Session) =
    lock session.stop_gate (fun () ->
        if Interlocked.CompareExchange(&session.stop_requested, 1, 0) = 0 then
            InputDiagnostics.record
                InputDiagnostics.EventKind.RawStopRequested
                (session.window_handle.ToInt64())
                (int64 session.native_thread_id)

            release_registration session.registration |> ignore
            signal_stop session)

let stop_internal (retry: bool) (session: Session) =
    lock session.stop_gate (fun () ->
        match session.stop_outcome with
        | Some outcome when not retry -> outcome
        | Some _
        | None ->
            request_stop session

            let errors = ResizeArray<string>()
            release_registration session.registration |> Seq.iter errors.Add
            signal_stop session

            for error in session.stop_signal_errors do
                errors.Add error

            let mutable terminated = not session.thread.IsAlive

            if not terminated then
                terminated <- session.stopped.Wait shutdown_timeout_ms

            if not terminated then
                if session.registration.relinquished then
                    match RawInputNative.post_quit session.native_thread_id with
                    | Ok() -> ()
                    | Error error -> errors.Add error

                terminated <- session.stopped.Wait shutdown_escalation_timeout_ms

            if terminated && session.thread.IsAlive then
                if not (session.thread.Join join_timeout_ms) then
                    terminated <- false
                    errors.Add "The raw-input thread signalled completion but did not terminate."

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
                errors.Add "The raw-input thread did not stop within two seconds."

            let outcome =
                { terminated = terminated
                  registration_relinquished = session.registration.relinquished
                  errors = List.ofSeq errors }

            InputDiagnostics.record
                (if terminated then
                     InputDiagnostics.EventKind.RawStopped
                 else
                     InputDiagnostics.EventKind.RawStopTimedOut)
                (if outcome.registration_relinquished then 1L else 0L)
                (int64 outcome.errors.Length)

            session.stop_outcome <- Some outcome

            if outcome.terminated && outcome.registration_relinquished then
                forget_recovery session
            else
                retain_for_recovery session

            outcome)

let stop (session: Session) = stop_internal false session

let runtime_failed (session: Session) =
    Option.isSome session.result.runtime_error

let retry_recovery () =
    let sessions = lock recoveryGate (fun () -> recoverySessions.ToArray())
    let registrations = lock recoveryGate (fun () -> recoveryRegistrations.ToArray())
    let errors = ResizeArray<string>()

    for session in sessions do
        let outcome = stop_internal true session

        for error in outcome.errors do
            errors.Add error

    for registration in registrations do
        match RawInputNative.release_mouse_registration registration with
        | Ok _ -> forget_registration_recovery registration
        | Error error -> errors.Add error

    let remaining =
        lock recoveryGate (fun () -> recoverySessions.Count + recoveryRegistrations.Count)

    struct (remaining, List.ofSeq errors)
