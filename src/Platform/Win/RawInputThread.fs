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

type Session =
    { thread: Thread
      window_handle: nativeint
      native_thread_id: uint32
      stopped: ManualResetEventSlim
      result: ThreadResult
      mutable stop_started: int }

type StartupState =
    { ready: ManualResetEventSlim
      stopped: ManualResetEventSlim
      mutable window_handle: nativeint
      mutable native_thread_id: uint32
      result: ThreadResult }

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
            let created = new RawInputReceiver(config, sessionMode, input, inputAvailable)
            receiver <- Some created
            startup.window_handle <- created.WindowHandle
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
                | None -> ()
            with error ->
                startup.result.shutdown_error <- Some error
        finally
            startup.stopped.Set()

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
          result = result }

    let thread =
        Thread(ThreadStart(fun () -> run_thread config sessionMode input inputAvailable startup))

    thread.IsBackground <- true
    thread.Name <- "RhinosCanFly raw input"
    thread.SetApartmentState(ApartmentState.STA)
    thread.Start()
    startup.ready.Wait()
    startup.ready.Dispose()

    match result.startup_error with
    | Some error ->
        startup.stopped.Wait()
        thread.Join()
        startup.stopped.Dispose()

        match result.shutdown_error with
        | Some shutdownError -> raise (AggregateException(error, shutdownError))
        | None -> raise error
    | None ->
        { thread = thread
          window_handle = startup.window_handle
          native_thread_id = startup.native_thread_id
          stopped = startup.stopped
          result = result
          stop_started = 0 }

let stop (session: Session) =
    if Interlocked.Exchange(&session.stop_started, 1) = 0 then
        let stopSignal =
            if session.stopped.IsSet then
                Ok()
            else
                match RawInputNative.post_stop session.window_handle with
                | Ok() -> Ok()
                | Error windowError ->
                    Debug.WriteLine $"RhinosCanFly: {windowError}"

                    match RawInputNative.post_quit session.native_thread_id with
                    | Ok() -> Ok()
                    | Error threadError -> Error $"Could not stop the raw-input thread: {windowError}; {threadError}"

        match stopSignal with
        | Error error ->
            Interlocked.Exchange(&session.stop_started, 0) |> ignore
            raise (InvalidOperationException error)
        | Ok() -> ()

        session.stopped.Wait()
        session.thread.Join()
        session.stopped.Dispose()

        match session.result.runtime_error, session.result.shutdown_error with
        | Some runtimeError, Some shutdownError -> raise (AggregateException(runtimeError, shutdownError))
        | Some error, None
        | None, Some error -> raise error
        | None, None -> ()
