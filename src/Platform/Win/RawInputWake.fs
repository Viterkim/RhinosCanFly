module RhinosCanFly.Platform.Win.RawInputWake

open System
open System.Threading

type State =
    { signal: AutoResetEvent
      handle: nativeint }

let create () =
    let signal = new AutoResetEvent(false)

    { signal = signal
      handle = signal.SafeWaitHandle.DangerousGetHandle() }

let clear (state: State) = state.signal.WaitOne(0) |> ignore

let signal (state: State) =
    try
        state.signal.Set() |> ignore
    with :? ObjectDisposedException ->
        ()

let wait (state: State) (timeoutMilliseconds: uint32) =
    Win32.wait_for_input_handle state.handle timeoutMilliseconds

let dispose (state: State) = state.signal.Dispose()
