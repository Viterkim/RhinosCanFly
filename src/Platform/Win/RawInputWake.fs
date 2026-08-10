module RhinosCanFly.Platform.Win.RawInputWake

open System
open System.Diagnostics
open System.Threading

type State = { mutable pending: int }

let create () = { pending = 0 }

let clear (state: State) =
    Interlocked.Exchange(&state.pending, 0) |> ignore

let signal (clearPending: Action) (state: State) =
    if
        Volatile.Read(&state.pending) = 0
        && Interlocked.CompareExchange(&state.pending, 1, 0) = 0
    then
        try
            Eto.Forms.Application.Instance.AsyncInvoke clearPending
        with error ->
            clear state
            Debug.WriteLine $"RhinosCanFly UI wake-up failed: {error.Message}"
