module RhinosCanFly.InputAccumulator

open System.Threading

type State =
    { mutable mouse_dx: int64
      mutable mouse_dy: int64
      mutable wheel_delta: int
      mutable pivot_toggle_requests: int
      mutable pivot_held: int
      mutable exit_requested: int }

let create () =
    { mouse_dx = 0L
      mouse_dy = 0L
      wheel_delta = 0
      pivot_toggle_requests = 0
      pivot_held = 0
      exit_requested = 0 }

let add_mouse (dx: int) (dy: int) (state: State) =
    Interlocked.Add(&state.mouse_dx, int64 dx) |> ignore
    Interlocked.Add(&state.mouse_dy, int64 dy) |> ignore

let add_wheel (delta: int) (state: State) =
    Interlocked.Add(&state.wheel_delta, delta) |> ignore

let request_exit (state: State) =
    Interlocked.Exchange(&state.exit_requested, 1) |> ignore

let request_pivot_toggle (state: State) =
    Interlocked.Increment(&state.pivot_toggle_requests) |> ignore

let set_pivot_held (held: bool) (state: State) =
    Volatile.Write(&state.pivot_held, if held then 1 else 0)

let drain_mouse (state: State) =
    struct (Interlocked.Exchange(&state.mouse_dx, 0L), Interlocked.Exchange(&state.mouse_dy, 0L))

let drain_wheel (state: State) =
    Interlocked.Exchange(&state.wheel_delta, 0)

let drain_pivot_toggles (state: State) =
    Interlocked.Exchange(&state.pivot_toggle_requests, 0)

let pivot_held (state: State) = Volatile.Read(&state.pivot_held) <> 0

let exit_requested (state: State) =
    Volatile.Read(&state.exit_requested) <> 0
