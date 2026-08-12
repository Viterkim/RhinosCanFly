module RhinosCanFly.Platform.Win.RawInputWake

open System
open System.Threading

type State =
    { signal: AutoResetEvent
      handle: nativeint
      mutable pending: int }

let create () =
    let signal = new AutoResetEvent(false)

    { signal = signal
      handle = signal.SafeWaitHandle.DangerousGetHandle()
      pending = 0 }

let clear (state: State) =
    Interlocked.Exchange(&state.pending, 0) |> ignore
    state.signal.WaitOne(0) |> ignore

let signal (state: State) =
    if Interlocked.CompareExchange(&state.pending, 1, 0) = 0 then
        InputDiagnostics.record_wake_signal ()

        try
            state.signal.Set() |> ignore
        with :? ObjectDisposedException ->
            ()

let wait (state: State) (timeoutMilliseconds: uint32) =
    Win32.wait_for_input_handle state.handle timeoutMilliseconds

let dispose (state: State) = state.signal.Dispose()
