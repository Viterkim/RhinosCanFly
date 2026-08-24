module RhinosCanFly.Platform.Win.RawInputWake

open System.Diagnostics
open System.Threading
open RhinosCanFly

type State =
    { window: RootWindow
      mutable disposed: int
      mutable pending: int }

let create (window: RootWindow) =
    let (RootWindow handle) = window

    if handle = nativeint 0 || not (Win32Native.IsWindow handle) then
        failwith "The Rhino window is unavailable."

    { window = window
      disposed = 0
      pending = 0 }

let acknowledge (state: State) =
    Interlocked.Exchange(&state.pending, 0) |> ignore

let acknowledge_if_pending (state: State) =
    Volatile.Read(&state.pending) <> 0
    && Interlocked.Exchange(&state.pending, 0) <> 0

let signal (state: State) =
    // Only post again after the UI has acknowledged the current work.
    if
        Volatile.Read(&state.disposed) = 0
        && Volatile.Read(&state.pending) = 0
        && Interlocked.CompareExchange(&state.pending, 1, 0) = 0
    then
        let (RootWindow window) = state.window

        if not (Win32Native.PostMessage(window, Win32Native.WM_NULL, nativeint 0, nativeint 0)) then
            Interlocked.Exchange(&state.pending, 0) |> ignore
            let error = Win32.last_error "PostMessage"
            Debug.WriteLine $"RhinosCanFly could not wake Rhino's main loop: {error}"

let dispose (state: State) =
    Interlocked.Exchange(&state.disposed, 1) |> ignore
    acknowledge state
