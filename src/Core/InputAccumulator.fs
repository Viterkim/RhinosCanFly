module RhinosCanFly.InputAccumulator

open System
open System.Threading

[<Literal>]
let RAW_MOUSE_BUTTON_EVENT_CAPACITY = 64

type State =
    { mutable mouse_xy: int64
      mutable wheel_delta: int64
      mutable pivot_toggle_requests: int
      mutable pan_toggle_requests: int
      mutable pivot_held: int
      mutable pan_held: int
      mutable retarget_request: int
      raw_mouse_button_events: RawMouseButtonTransition array
      mutable raw_mouse_button_event_write: int64
      mutable raw_mouse_button_event_read: int64
      mutable raw_mouse_button_event_overflow: int
      mutable exit_reason: FlightExitReason option
      mutable work_revision: int64 }

[<Struct>]
type WorkRevision = WorkRevision of int64

let create () =
    { mouse_xy = 0L
      wheel_delta = 0L
      pivot_toggle_requests = 0
      pan_toggle_requests = 0
      pivot_held = 0
      pan_held = 0
      retarget_request = int RetargetMode.Off
      raw_mouse_button_events = Array.zeroCreate RAW_MOUSE_BUTTON_EVENT_CAPACITY
      raw_mouse_button_event_write = 0L
      raw_mouse_button_event_read = 0L
      raw_mouse_button_event_overflow = 0
      exit_reason = None
      work_revision = 0L }

let mark_work_available (state: State) =
    Interlocked.Increment(&state.work_revision) |> ignore

let pack_mouse (x: int32) (y: int32) =
    int64 (uint64 (uint32 x) ||| (uint64 (uint32 y) <<< 32))

let unpack_mouse (packed: int64) =
    struct (int32 (uint32 packed), int32 (uint32 (uint64 packed >>> 32)))

let saturating_add (current: int32) (delta: int32) =
    let sum = int64 current + int64 delta

    if sum > int64 Int32.MaxValue then Int32.MaxValue
    elif sum < int64 Int32.MinValue then Int32.MinValue
    else int32 sum

let add_mouse (dx: int) (dy: int) (state: State) =
    if dx <> 0 || dy <> 0 then
        let mutable updated = false

        while not updated do
            let current = Volatile.Read(&state.mouse_xy)
            let struct (currentX, currentY) = unpack_mouse current

            let next =
                pack_mouse (saturating_add currentX (int32 dx)) (saturating_add currentY (int32 dy))

            updated <- Interlocked.CompareExchange(&state.mouse_xy, next, current) = current

        mark_work_available state

let add_wheel (delta: int) (state: State) =
    if delta <> 0 then
        Interlocked.Add(&state.wheel_delta, int64 delta) |> ignore
        mark_work_available state

let request_exit (reason: FlightExitReason) (state: State) =
    let previous = Interlocked.CompareExchange(&state.exit_reason, Some reason, None)

    if Option.isNone previous then
        mark_work_available state

let request_pivot_toggle (state: State) =
    Interlocked.Increment(&state.pivot_toggle_requests) |> ignore
    mark_work_available state

let request_pan_toggle (state: State) =
    Interlocked.Increment(&state.pan_toggle_requests) |> ignore
    mark_work_available state

let set_pivot_held (held: bool) (state: State) =
    let value = if held then 1 else 0

    if Interlocked.Exchange(&state.pivot_held, value) <> value then
        mark_work_available state

let set_pan_held (held: bool) (state: State) =
    let value = if held then 1 else 0

    if Interlocked.Exchange(&state.pan_held, value) <> value then
        mark_work_available state

let request_retarget (mode: RetargetMode) (state: State) =
    if mode <> RetargetMode.Off then
        Interlocked.Exchange(&state.retarget_request, int mode) |> ignore
        mark_work_available state

let add_raw_mouse_button_event (event: RawMouseButtonEvent) (timestamp: int64) (state: State) =
    if event <> RawMouseButtonEvent.None then
        // One raw worker fills each slot before the UI can see the new write position.
        let write = Volatile.Read(&state.raw_mouse_button_event_write)
        let read = Volatile.Read(&state.raw_mouse_button_event_read)

        if write - read >= int64 state.raw_mouse_button_events.Length then
            Interlocked.Exchange(&state.raw_mouse_button_event_overflow, 1) |> ignore
        else
            let index = int (write % int64 state.raw_mouse_button_events.Length)
            state.raw_mouse_button_events[index] <- { event = event; timestamp = timestamp }

            Volatile.Write(&state.raw_mouse_button_event_write, write + 1L)

        mark_work_available state

let drain_mouse (state: State) =
    let struct (x, y) = unpack_mouse (Interlocked.Exchange(&state.mouse_xy, 0L))
    struct (int64 x, int64 y)

let drain_wheel (state: State) =
    Interlocked.Exchange(&state.wheel_delta, 0L)

let drain_pivot_toggles (state: State) =
    Interlocked.Exchange(&state.pivot_toggle_requests, 0)

let drain_pan_toggles (state: State) =
    Interlocked.Exchange(&state.pan_toggle_requests, 0)

let pivot_held (state: State) = Volatile.Read(&state.pivot_held) <> 0

let pan_held (state: State) = Volatile.Read(&state.pan_held) <> 0

let drain_retarget_request (state: State) =
    enum<RetargetMode> (Interlocked.Exchange(&state.retarget_request, int RetargetMode.Off))

let try_drain_raw_mouse_button_event (state: State) =
    let read = Volatile.Read(&state.raw_mouse_button_event_read)

    if read >= Volatile.Read(&state.raw_mouse_button_event_write) then
        ValueNone
    else
        let index = int (read % int64 state.raw_mouse_button_events.Length)
        let transition = state.raw_mouse_button_events[index]
        Volatile.Write(&state.raw_mouse_button_event_read, read + 1L)
        ValueSome transition

let drain_raw_mouse_button_event_overflow (state: State) =
    Interlocked.Exchange(&state.raw_mouse_button_event_overflow, 0) <> 0

let raw_mouse_button_event_pending (state: State) =
    Volatile.Read(&state.raw_mouse_button_event_read) < Volatile.Read(&state.raw_mouse_button_event_write)

let exit_reason (state: State) = Volatile.Read(&state.exit_reason)

let work_revision (state: State) =
    WorkRevision(Volatile.Read(&state.work_revision))

let work_pending_since (WorkRevision observed: WorkRevision) (state: State) =
    Option.isSome (exit_reason state)
    || Volatile.Read(&state.work_revision) <> observed
    || raw_mouse_button_event_pending state
    || Volatile.Read(&state.raw_mouse_button_event_overflow) <> 0

let discard_transient_input (state: State) =
    Interlocked.Exchange(&state.mouse_xy, 0L) |> ignore
    Interlocked.Exchange(&state.wheel_delta, 0L) |> ignore
    Interlocked.Exchange(&state.pivot_toggle_requests, 0) |> ignore
    Interlocked.Exchange(&state.pan_toggle_requests, 0) |> ignore
    Interlocked.Exchange(&state.retarget_request, int RetargetMode.Off) |> ignore

    let buttonWrite = Volatile.Read(&state.raw_mouse_button_event_write)
    Volatile.Write(&state.raw_mouse_button_event_read, buttonWrite)
    Interlocked.Exchange(&state.raw_mouse_button_event_overflow, 0) |> ignore
