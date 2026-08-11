module RhinosCanFly.Platform.Win.RawInputWake

open System.Diagnostics
open System.Threading
open RhinosCanFly

type State =
    { window: RootWindow
      mutable pending: int }

let create (window: RootWindow) = { window = window; pending = 0 }

let clear (state: State) =
    Interlocked.Exchange(&state.pending, 0) |> ignore

let signal (state: State) =
    if
        Volatile.Read(&state.pending) = 0
        && Interlocked.CompareExchange(&state.pending, 1, 0) = 0
    then
        let (RootWindow window) = state.window

        if not (Win32Native.PostMessage(window, Win32Native.WM_NULL, nativeint 0, nativeint 0)) then
            let error = Win32.last_error "PostMessage(WM_NULL)"
            clear state
            Debug.WriteLine $"RhinosCanFly UI wake-up failed: {error}"
