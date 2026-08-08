module RhinosCanFly.Platform.Win.RawInputWake

open System
open System.Diagnostics
open System.Threading

type State = { mutable pending: int }

let create () = { pending = 0 }

let signal (state: State) =
    if Interlocked.CompareExchange(&state.pending, 1, 0) = 0 then
        try
            Eto.Forms.Application.Instance.AsyncInvoke(Action(fun () -> ()))
        with error ->
            Interlocked.Exchange(&state.pending, 0) |> ignore
            Debug.WriteLine $"RhinosCanFly UI wake-up failed: {error.Message}"

let reset (state: State) =
    Interlocked.Exchange(&state.pending, 0) |> ignore
