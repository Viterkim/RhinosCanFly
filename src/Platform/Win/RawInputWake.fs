module RhinosCanFly.Platform.Win.RawInputWake

open System
open System.Threading

type State =
    { signal: AutoResetEvent
      handle: nativeint
      mutable handle_referenced: bool
      mutable pending: int }

let create () =
    let signal = new AutoResetEvent(false)
    let mutable referenced = false

    try
        signal.SafeWaitHandle.DangerousAddRef &referenced
    with error ->
        if referenced then
            signal.SafeWaitHandle.DangerousRelease()

        signal.Dispose()
        raise error

    if not referenced then
        signal.Dispose()
        failwith "The raw-input wake handle could not be referenced."

    { signal = signal
      handle = signal.SafeWaitHandle.DangerousGetHandle()
      handle_referenced = true
      pending = 0 }

let acknowledge (state: State) =
    Interlocked.Exchange(&state.pending, 0) |> ignore

let signal (state: State) =
    if Interlocked.CompareExchange(&state.pending, 1, 0) = 0 then
        InputDiagnostics.record_wake_signal ()

        try
            state.signal.Set() |> ignore
        with :? ObjectDisposedException ->
            ()

let wait (state: State) (timeoutMilliseconds: uint32) =
    Win32.wait_for_input_handle state.handle timeoutMilliseconds

let dispose (state: State) =
    if state.handle_referenced then
        state.handle_referenced <- false

        try
            state.signal.SafeWaitHandle.DangerousRelease()
        finally
            state.signal.Dispose()
    else
        state.signal.Dispose()
