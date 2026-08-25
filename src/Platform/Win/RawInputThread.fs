module RhinosCanFly.Platform.Win.RawInputThread

open System
open System.Collections.Concurrent
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

type SessionRequest =
    { id: int64
      config: RawMouseInputConfig
      session_mode: FlightSessionMode
      input: InputAccumulator.State
      input_available: Action
      ready: ManualResetEventSlim
      stopped: ManualResetEventSlim
      result: ThreadResult
      mutable registration: RawInputNative.MouseRegistrationLease option
      mutable cancelled: int
      mutable ready_disposed: bool
      mutable stopped_disposed: bool }

type WorkerCommand =
    | StartSession of SessionRequest
    | StopSession of int64
    | FinishSession of int64
    | ShutdownWorker

type CommandSubmission =
    | Posted
    | QueuedWithoutWake of string

type SessionOwnership =
    | NoSession
    | SessionStarting
    | SessionActive of int64

type WorkerState =
    { thread: Thread
      ready: ManualResetEventSlim
      stopped: ManualResetEventSlim
      commands: ConcurrentQueue<WorkerCommand>
      command_gate: obj
      result: ThreadResult
      mutable window_handle: nativeint
      mutable accepting_commands: bool
      mutable cancelled: int
      mutable ready_disposed: bool
      mutable stopped_disposed: bool }

type Session =
    { worker: WorkerState
      request: SessionRequest
      stop_gate: obj
      mutable stop_request_sent: bool
      mutable stop_outcome: StopOutcome option }

[<Literal>]
let STARTUP_TIMEOUT_MS = 250

[<Literal>]
let STOP_OBSERVATION_MS = 1000

[<Literal>]
let JOIN_TIMEOUT_MS = 100

let recoveryGate = obj ()
let recoverySessions = ResizeArray<Session>()
let workerGate = obj ()
let sessionGate = obj ()
let mutable workerState: WorkerState option = None
let mutable workerShutDown = false
let mutable sessionOwnership = NoSession
let mutable nextSessionId = 0L

let startup_error (result: ThreadResult) = Volatile.Read(&result.startup_error)
let runtime_error (result: ThreadResult) = Volatile.Read(&result.runtime_error)
let shutdown_error (result: ThreadResult) = Volatile.Read(&result.shutdown_error)

let record_startup_error (result: ThreadResult) (error: exn) =
    Interlocked.CompareExchange(&result.startup_error, Some error, None) |> ignore

let record_runtime_error (result: ThreadResult) (error: exn) =
    Interlocked.CompareExchange(&result.runtime_error, Some error, None) |> ignore

let record_shutdown_error (result: ThreadResult) (error: exn) =
    Interlocked.CompareExchange(&result.shutdown_error, Some error, None) |> ignore

let recovery_pending () =
    lock recoveryGate (fun () -> recoverySessions.Count > 0)

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

let exception_messages (error: exn) =
    match error with
    | :? AggregateException as aggregate ->
        aggregate.Flatten().InnerExceptions
        |> Seq.map (fun (inner: exn) -> inner.Message)
        |> List.ofSeq
    | _ -> [ error.Message ]

let stop_outcome_is_clean (outcome: StopOutcome) =
    outcome.terminated
    && outcome.registration_relinquished
    && not outcome.previous_registration_lost

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

let publish_runtime_failure (request: SessionRequest) (error: exn) =
    record_runtime_error request.result error
    InputAccumulator.request_exit (SessionFailure(error.ToString())) request.input
    request.input_available.Invoke()

let run_worker (worker: WorkerState) =
    let mutable receiver: RawInputReceiver option = None
    let mutable currentRequest: SessionRequest option = None
    let mutable shutdownRequested = false
    let mutable readyPublished = false

    let finish_current (sessionId: int64) =
        match currentRequest with
        | Some request when request.id = sessionId ->
            match receiver with
            | Some created ->
                try
                    created.ReleaseSession()
                with error ->
                    record_shutdown_error request.result error
            | None -> ()

            currentRequest <- None
            request.stopped.Set()

            if shutdownRequested then
                Application.ExitThread()
        | Some _
        | None -> ()

    let queue_finish (sessionId: int64) =
        worker.commands.Enqueue(FinishSession sessionId)

        match RawInputNative.post_control worker.window_handle with
        | Ok() -> ()
        | Error error ->
            match currentRequest with
            | Some request when request.id = sessionId ->
                record_shutdown_error request.result (InvalidOperationException error)
            | Some _
            | None -> ()

            Application.ExitThread()

    let begin_session (request: SessionRequest) =
        if Volatile.Read(&request.cancelled) <> 0 then
            record_startup_error request.result (OperationCanceledException "Raw-input startup was cancelled.")
            request.ready.Set()
            request.stopped.Set()
        else
            match currentRequest with
            | Some _ ->
                let error = InvalidOperationException "Another raw-input session is already active."
                record_startup_error request.result error
                request.ready.Set()
                request.stopped.Set()
            | None ->
                try
                    let registrationReady =
                        Action<RawInputNative.MouseRegistrationLease>
                            (fun (lease: RawInputNative.MouseRegistrationLease) -> request.registration <- Some lease)

                    let runtimeFailed =
                        Action<exn>(fun (error: exn) -> publish_runtime_failure request error)

                    let sessionFinished = Action(fun () -> queue_finish request.id)
                    currentRequest <- Some request

                    let startupError =
                        match receiver with
                        | Some created ->
                            created.StartSession(
                                request.config,
                                request.session_mode,
                                request.input,
                                request.input_available,
                                registrationReady,
                                runtimeFailed,
                                sessionFinished
                            )
                        | None -> Some(InvalidOperationException "The raw-input worker is unavailable.")

                    match startupError with
                    | Some error -> record_startup_error request.result error
                    | None -> ()

                    request.ready.Set()

                    if Option.isSome startupError then
                        match receiver with
                        | Some created -> created.RequestStop()
                        | None -> request.stopped.Set()
                with error ->
                    record_startup_error request.result error
                    request.ready.Set()
                    request.stopped.Set()

    let stop_session (sessionId: int64) =
        match currentRequest with
        | Some request when request.id = sessionId ->
            match receiver with
            | Some created -> created.RequestStop()
            | None -> request.stopped.Set()
        | Some _
        | None -> ()

    let process_commands () =
        let mutable command = Unchecked.defaultof<WorkerCommand>
        let mutable draining = true

        while draining do
            if worker.commands.TryDequeue(&command) then
                match command with
                | StartSession request ->
                    if shutdownRequested then
                        record_startup_error
                            request.result
                            (InvalidOperationException "The raw-input worker is shutting down.")

                        request.ready.Set()
                        request.stopped.Set()
                    else
                        begin_session request
                | StopSession sessionId -> stop_session sessionId
                | FinishSession sessionId -> finish_current sessionId
                | ShutdownWorker ->
                    shutdownRequested <- true

                    match currentRequest with
                    | Some request -> stop_session request.id
                    | None -> Application.ExitThread()
            else
                draining <- false

    let process_commands_safely () =
        try
            process_commands ()
        with error ->
            record_runtime_error worker.result error

            match currentRequest with
            | Some request -> publish_runtime_failure request error
            | None -> ()

            Application.ExitThread()

    try
        try
            let created = new RawInputReceiver(Action process_commands_safely)
            receiver <- Some created

            lock worker.command_gate (fun () ->
                worker.window_handle <- created.WindowHandle
                worker.accepting_commands <- true)

            readyPublished <- true
            worker.ready.Set()

            if Volatile.Read(&worker.cancelled) = 0 then
                Application.Run()
        with error ->
            if not readyPublished then
                record_startup_error worker.result error
                readyPublished <- true
                worker.ready.Set()
            else
                record_runtime_error worker.result error

                match currentRequest with
                | Some request -> publish_runtime_failure request error
                | None -> ()
    finally
        lock worker.command_gate (fun () -> worker.accepting_commands <- false)

        match receiver with
        | Some created ->
            while not created.RegistrationRelinquished do
                created.RequestStop()

                if not created.RegistrationRelinquished then
                    Thread.Sleep RawInputNative.REGISTRATION_RETRY_INTERVAL_MS

            match currentRequest with
            | Some request ->
                try
                    created.ReleaseSession()
                with error ->
                    record_shutdown_error request.result error

                request.stopped.Set()
            | None -> ()
        | None -> ()

        let pendingError =
            match runtime_error worker.result with
            | Some error -> error
            | None -> InvalidOperationException "The raw-input worker stopped before processing a session."

        let mutable pendingCommand = Unchecked.defaultof<WorkerCommand>

        while worker.commands.TryDequeue(&pendingCommand) do
            match pendingCommand with
            | StartSession request ->
                record_startup_error request.result pendingError

                request.ready.Set()
                request.stopped.Set()
            | StopSession _
            | FinishSession _
            | ShutdownWorker -> ()

        match receiver with
        | Some created ->
            try
                created.ReleaseResources()
            with error ->
                record_shutdown_error worker.result error
        | None -> ()

        worker.stopped.Set()

let create_worker () =
    let mutable created = Unchecked.defaultof<WorkerState>
    let thread = Thread(ThreadStart(fun () -> run_worker created))

    let worker =
        { thread = thread
          ready = new ManualResetEventSlim(false)
          stopped = new ManualResetEventSlim(false)
          commands = ConcurrentQueue<WorkerCommand>()
          command_gate = obj ()
          result =
            { startup_error = None
              runtime_error = None
              shutdown_error = None }
          window_handle = nativeint 0
          accepting_commands = false
          cancelled = 0
          ready_disposed = false
          stopped_disposed = false }

    created <- worker
    thread.IsBackground <- true
    thread.Name <- "RhinosCanFly raw input"
    thread.SetApartmentState(ApartmentState.STA)
    thread.Start()
    worker

let dispose_worker_events (worker: WorkerState) =
    if not worker.ready_disposed then
        worker.ready.Dispose()
        worker.ready_disposed <- true

    if not worker.stopped_disposed then
        worker.stopped.Dispose()
        worker.stopped_disposed <- true

let ensure_worker () =
    lock workerGate (fun () ->
        if workerShutDown then
            let message = "The raw-input worker has shut down."
            raise (StartFailureException(message, true, InvalidOperationException message))

        let worker =
            match workerState with
            | Some current when not current.stopped.IsSet -> current
            | Some current ->
                dispose_worker_events current
                let replacement = create_worker ()
                workerState <- Some replacement
                replacement
            | None ->
                let created = create_worker ()
                workerState <- Some created
                created

        if not (worker.ready.Wait STARTUP_TIMEOUT_MS) then
            Interlocked.Exchange(&worker.cancelled, 1) |> ignore
            let terminated = worker.stopped.Wait STOP_OBSERVATION_MS

            if terminated then
                dispose_worker_events worker
                workerState <- None

            let message = "The raw-input worker did not become ready within 250 ms."
            raise (StartFailureException(message, not terminated, TimeoutException message))

        let fail_worker (error: exn) =
            let terminated = worker.stopped.Wait STOP_OBSERVATION_MS

            if terminated then
                dispose_worker_events worker
                workerState <- None

            raise (StartFailureException(error.Message, not terminated, error))

        match startup_error worker.result with
        | Some error -> fail_worker error
        | None -> ()

        match runtime_error worker.result with
        | Some error -> fail_worker error
        | None -> ()

        if worker.stopped.IsSet then
            let message = "The raw-input worker stopped during startup."
            dispose_worker_events worker
            workerState <- None
            raise (StartFailureException(message, false, InvalidOperationException message))

        worker)

let prepare () =
    try
        ensure_worker () |> ignore
        Ok()
    with error ->
        Error error.Message

let enqueue_command (worker: WorkerState) (command: WorkerCommand) =
    lock worker.command_gate (fun () ->
        if not worker.accepting_commands || worker.stopped.IsSet then
            Error "The raw-input worker is no longer accepting commands."
        else
            worker.commands.Enqueue command

            match RawInputNative.post_control worker.window_handle with
            | Ok() -> Ok Posted
            | Error error -> Ok(QueuedWithoutWake error))

let clear_active_session (sessionId: int64) =
    lock sessionGate (fun () ->
        match sessionOwnership with
        | SessionActive active when active = sessionId -> sessionOwnership <- NoSession
        | SessionActive _
        | SessionStarting
        | NoSession -> ())

let request_stop_core (session: Session) =
    if session.stop_request_sent then
        Ok()
    elif session.request.stopped.IsSet then
        session.stop_request_sent <- true
        Ok()
    else
        match enqueue_command session.worker (StopSession session.request.id) with
        | Ok Posted ->
            session.stop_request_sent <- true
            Ok()
        | Ok(QueuedWithoutWake error) -> Error error
        | Error _ when session.worker.stopped.IsSet ->
            session.stop_request_sent <- true
            Ok()
        | Error error -> Error error

let request_stop (session: Session) =
    lock session.stop_gate (fun () -> request_stop_core session)

let stop_internal (attempt: StopAttempt) (session: Session) =
    lock session.stop_gate (fun () ->
        match session.stop_outcome with
        | Some outcome when attempt = InitialStop -> outcome
        | Some _
        | None ->
            let errors = ResizeArray<string>()

            match request_stop_core session with
            | Ok() -> ()
            | Error error -> errors.Add error

            let terminated = session.request.stopped.Wait STOP_OBSERVATION_MS

            let cleanupComplete =
                terminated && try_complete_registration_cleanup session.request.registration

            let previousRegistrationLost =
                match session.request.registration with
                | Some registration -> registration.previous_registration_lost
                | None -> false

            if cleanupComplete && not session.request.stopped_disposed then
                session.request.stopped.Dispose()
                session.request.stopped_disposed <- true

            if cleanupComplete && not session.request.ready_disposed then
                session.request.ready.Dispose()
                session.request.ready_disposed <- true

            match runtime_error session.request.result with
            | Some error -> exception_messages error |> Seq.iter errors.Add
            | None -> ()

            match shutdown_error session.request.result with
            | Some error -> exception_messages error |> Seq.iter errors.Add
            | None -> ()

            if not terminated then
                errors.Add "The raw-input session is still cleaning up in the background."

            let registrationRelinquished =
                match session.request.registration with
                | Some registration -> registration.relinquished
                | None -> true

            let outcome =
                { terminated = terminated
                  registration_relinquished = registrationRelinquished
                  previous_registration_lost = previousRegistrationLost
                  errors = List.ofSeq errors }

            session.stop_outcome <- Some outcome

            if stop_outcome_is_clean outcome then
                forget_session session
                clear_active_session session.request.id
            else
                retain_session session

            outcome)

let stop (session: Session) = stop_internal InitialStop session

let start
    (config: RawMouseInputConfig)
    (sessionMode: FlightSessionMode)
    (input: InputAccumulator.State)
    (inputAvailable: Action)
    =
    if recovery_pending () then
        let message =
            "A previous raw-input session still needs cleanup. Run RhinosCanFlyInputRecover or restart Rhino."

        raise (StartFailureException(message, true, InvalidOperationException message))

    let reserved =
        lock sessionGate (fun () ->
            match sessionOwnership with
            | NoSession ->
                sessionOwnership <- SessionStarting
                true
            | SessionStarting
            | SessionActive _ -> false)

    if not reserved then
        let message = "Another raw-input session is already active."
        raise (StartFailureException(message, false, InvalidOperationException message))

    try
        let worker = ensure_worker ()
        let sessionId = Interlocked.Increment(&nextSessionId)

        let result =
            { startup_error = None
              runtime_error = None
              shutdown_error = None }

        let request =
            { id = sessionId
              config = config
              session_mode = sessionMode
              input = input
              input_available = inputAvailable
              ready = new ManualResetEventSlim(false)
              stopped = new ManualResetEventSlim(false)
              result = result
              registration = None
              cancelled = 0
              ready_disposed = false
              stopped_disposed = false }

        let session =
            { worker = worker
              request = request
              stop_gate = obj ()
              stop_request_sent = false
              stop_outcome = None }

        match enqueue_command worker (StartSession request) with
        | Error error ->
            Interlocked.Exchange(&request.cancelled, 1) |> ignore
            request.ready.Set()
            request.stopped.Set()

            let outcome = stop_internal RecoveryRetry session

            let restartRequired = not (stop_outcome_is_clean outcome)

            raise (StartFailureException(error, restartRequired, InvalidOperationException error))
        | Ok(QueuedWithoutWake error) ->
            Interlocked.Exchange(&request.cancelled, 1) |> ignore
            let outcome = stop_internal RecoveryRetry session
            let restartRequired = not (stop_outcome_is_clean outcome)
            raise (StartFailureException(error, restartRequired, InvalidOperationException error))
        | Ok Posted -> ()

        if not (request.ready.Wait STARTUP_TIMEOUT_MS) then
            Interlocked.Exchange(&request.cancelled, 1) |> ignore
            let outcome = stop_internal RecoveryRetry session

            let message =
                "The raw-input session did not become ready within 250 ms. Cleanup is continuing on the worker."

            let restartRequired = not (stop_outcome_is_clean outcome)

            raise (StartFailureException(message, restartRequired, TimeoutException message))

        if not request.ready_disposed then
            request.ready.Dispose()
            request.ready_disposed <- true

        match startup_error result with
        | Some error ->
            let outcome = stop_internal RecoveryRetry session

            let errors =
                exception_messages error
                @ (shutdown_error result |> Option.map exception_messages |> Option.defaultValue [])

            let restartRequired = not (stop_outcome_is_clean outcome)

            raise (StartFailureException(String.concat "; " errors, restartRequired, error))
        | None ->
            match request.registration with
            | None ->
                let outcome = stop_internal RecoveryRetry session
                let message = "The raw-input session started without a mouse registration."

                let restartRequired = not (stop_outcome_is_clean outcome)

                raise (StartFailureException(message, restartRequired, InvalidOperationException message))
            | Some _ when request.stopped.IsSet ->
                let outcome = stop_internal RecoveryRetry session
                let message = "The raw-input session stopped before startup completed."

                let restartRequired = not (stop_outcome_is_clean outcome)

                raise (StartFailureException(message, restartRequired, InvalidOperationException message))
            | Some _ ->
                lock sessionGate (fun () -> sessionOwnership <- SessionActive sessionId)
                session
    finally
        lock sessionGate (fun () ->
            match sessionOwnership with
            | SessionStarting -> sessionOwnership <- NoSession
            | NoSession
            | SessionActive _ -> ())

let runtime_failed (session: Session) =
    Option.isSome (runtime_error session.request.result)

let registration_is_current (session: Session) =
    match session.request.registration with
    | Some registration -> RawInputNative.mouse_registration_is_current registration
    | None -> Ok false

let retry_recovery () =
    let sessions = lock recoveryGate (fun () -> recoverySessions.ToArray())
    let errors = ResizeArray<string>()

    for session in sessions do
        let outcome = stop_internal RecoveryRetry session

        for error in outcome.errors do
            errors.Add error

    let remaining = lock recoveryGate (fun () -> recoverySessions.Count)
    struct (remaining, List.ofSeq errors)

let shutdown () =
    lock workerGate (fun () ->
        workerShutDown <- true
        let errors = ResizeArray<string>()

        match workerState with
        | None -> ()
        | Some worker ->
            match enqueue_command worker ShutdownWorker with
            | Ok Posted -> ()
            | Ok(QueuedWithoutWake error) -> errors.Add error
            | Error _ when worker.stopped.IsSet -> ()
            | Error error -> errors.Add error

            let terminated = worker.stopped.Wait STOP_OBSERVATION_MS

            if terminated && worker.thread.IsAlive then
                worker.thread.Join JOIN_TIMEOUT_MS |> ignore

            match runtime_error worker.result with
            | Some error -> exception_messages error |> Seq.iter errors.Add
            | None -> ()

            match shutdown_error worker.result with
            | Some error -> exception_messages error |> Seq.iter errors.Add
            | None -> ()

            if terminated then
                dispose_worker_events worker
                workerState <- None
            else
                errors.Add "The raw-input worker is still shutting down."

        List.ofSeq errors)
