module RhinosCanFly.InputAccumulator

open System.Threading

type State =
    { mutable mouse_xy: int64
      mutable wheel_delta: int64
      mutable pivot_toggle_requests: int
      mutable pivot_held: int
      mutable exit_requested: int }

let create () =
    { mouse_xy = 0L
      wheel_delta = 0L
      pivot_toggle_requests = 0
      pivot_held = 0
      exit_requested = 0 }

let pack_mouse (x: int32) (y: int32) =
    int64 (uint64 (uint32 x) ||| (uint64 (uint32 y) <<< 32))

let unpack_mouse (packed: int64) =
    struct (int32 (uint32 packed), int32 (uint32 (uint64 packed >>> 32)))

let add_mouse (dx: int) (dy: int) (state: State) =
    if dx <> 0 || dy <> 0 then
        let mutable updated = false

        while not updated do
            let current = Volatile.Read(&state.mouse_xy)
            let struct (currentX, currentY) = unpack_mouse current
            let next = pack_mouse (currentX + int32 dx) (currentY + int32 dy)
            updated <- Interlocked.CompareExchange(&state.mouse_xy, next, current) = current

let add_wheel (delta: int) (state: State) =
    Interlocked.Add(&state.wheel_delta, int64 delta) |> ignore

let request_exit (state: State) =
    Interlocked.Exchange(&state.exit_requested, 1) |> ignore

let request_pivot_toggle (state: State) =
    Interlocked.Increment(&state.pivot_toggle_requests) |> ignore

let set_pivot_held (held: bool) (state: State) =
    Volatile.Write(&state.pivot_held, if held then 1 else 0)

let drain_mouse (state: State) =
    let struct (x, y) = unpack_mouse (Interlocked.Exchange(&state.mouse_xy, 0L))
    struct (int64 x, int64 y)

let drain_wheel (state: State) =
    Interlocked.Exchange(&state.wheel_delta, 0L)

let drain_pivot_toggles (state: State) =
    Interlocked.Exchange(&state.pivot_toggle_requests, 0)

let pivot_held (state: State) = Volatile.Read(&state.pivot_held) <> 0

let exit_requested (state: State) =
    Volatile.Read(&state.exit_requested) <> 0

let discard_transient_input (state: State) =
    Interlocked.Exchange(&state.mouse_xy, 0L) |> ignore
    Interlocked.Exchange(&state.wheel_delta, 0L) |> ignore
    Interlocked.Exchange(&state.pivot_toggle_requests, 0) |> ignore
