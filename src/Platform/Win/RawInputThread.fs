module RhinosCanFly.Platform.Win.RawInputThread

open System
open System.Diagnostics
open System.Threading
open System.Windows.Forms
open RhinosCanFly

type Session =
    { thread: Thread
      window_handle: nativeint
      native_thread_id: uint32
      stopped: ManualResetEventSlim
      mutable stop_started: int }

type StartupState =
    { ready: ManualResetEventSlim
      stopped: ManualResetEventSlim
      mutable window_handle: nativeint
      mutable native_thread_id: uint32
      mutable error: exn option }

let run_thread (config: FlyConfig) (input: InputAccumulator.State) (inputAvailable: Action) (startup: StartupState) =
    let mutable receiver: RawInputReceiver option = None
    let mutable startupComplete = false

    try
        try
            startup.native_thread_id <- RawInputNative.GetCurrentThreadId()
            let created = new RawInputReceiver(config, input, inputAvailable)
            receiver <- Some created
            startup.window_handle <- created.WindowHandle
            startupComplete <- true
            startup.ready.Set()
            Application.Run()
        with error ->
            if startupComplete then
                Debug.WriteLine $"RhinosCanFly raw-input thread failed: {error.Message}"
            else
                startup.error <- Some error
                startup.ready.Set()
    finally
        match receiver with
        | Some created -> created.ReleaseResources()
        | None -> ()

        startup.stopped.Set()

let start (config: FlyConfig) (input: InputAccumulator.State) (inputAvailable: Action) =
    let startup =
        { ready = new ManualResetEventSlim(false)
          stopped = new ManualResetEventSlim(false)
          window_handle = nativeint 0
          native_thread_id = 0u
          error = None }

    let thread =
        Thread(ThreadStart(fun () -> run_thread config input inputAvailable startup))

    thread.IsBackground <- true
    thread.Name <- "RhinosCanFly raw input"
    thread.SetApartmentState(ApartmentState.STA)
    thread.Start()
    startup.ready.Wait()
    startup.ready.Dispose()

    match startup.error with
    | Some error ->
        startup.stopped.Wait()
        startup.stopped.Dispose()
        raise error
    | None ->
        { thread = thread
          window_handle = startup.window_handle
          native_thread_id = startup.native_thread_id
          stopped = startup.stopped
          stop_started = 0 }

let stop (session: Session) =
    if Interlocked.Exchange(&session.stop_started, 1) = 0 then
        match RawInputNative.post_stop session.window_handle with
        | Ok() -> ()
        | Error windowError ->
            Debug.WriteLine $"RhinosCanFly: {windowError}"

            match RawInputNative.post_quit session.native_thread_id with
            | Ok() -> ()
            | Error threadError ->
                raise (InvalidOperationException($"Could not stop the raw-input thread: {windowError}; {threadError}"))

        session.stopped.Wait()
        session.thread.Join()
        session.stopped.Dispose()
