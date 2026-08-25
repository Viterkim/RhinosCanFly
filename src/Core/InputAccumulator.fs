module RhinosCanFly.InputAccumulator

open System
open System.Threading

[<Literal>]
let TIMELINE_EVENT_CAPACITY = 128

[<Flags>]
type KeyboardAction =
    | None = 0
    | PivotToggle = 1
    | PanToggle = 2
    | PivotHoldStarted = 4
    | PivotHoldEnded = 8
    | PanHoldStarted = 16
    | PanHoldEnded = 32
    | BoostToggle = 64
    | SlowToggle = 128
    | SpeedIncrease = 256
    | SpeedDecrease = 512
    | ProjectionToggle = 1024
    | RetargetAllViews = 2048
    | RetargetOtherViews = 4096
    | Exit = 8192
    | CancelAndRestore = 16384

[<RequireQualifiedAccess>]
type TimelineEventKind =
    | Movement = 0
    | Wheel = 1
    | RawMouseButton = 2
    | KeyboardActions = 3

[<Struct>]
type TimelineEvent =
    { kind: TimelineEventKind
      dx: int64
      dy: int64
      wheel: int64
      button: RawMouseButtonTransition
      keyboard_actions: KeyboardAction
      timestamp: int64 }

type State =
    { mutable mouse_xy: int64
      timeline_gate: obj
      timeline_events: TimelineEvent array
      mutable timeline_write: int64
      mutable timeline_read: int64
      mutable timeline_overflow: int
      mutable exit_reason: FlightExitReason option
      mutable work_revision: int64 }

[<Struct>]
type WorkRevision = WorkRevision of int64

let create () =
    { mouse_xy = 0L
      timeline_gate = obj ()
      timeline_events = Array.zeroCreate TIMELINE_EVENT_CAPACITY
      timeline_write = 0L
      timeline_read = 0L
      timeline_overflow = 0
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

let request_exit (reason: FlightExitReason) (state: State) =
    let previous = Interlocked.CompareExchange(&state.exit_reason, Some reason, None)

    if Option.isNone previous then
        mark_work_available state

let movement_event (dx: int64) (dy: int64) =
    { kind = TimelineEventKind.Movement
      dx = dx
      dy = dy
      wheel = 0L
      button = Unchecked.defaultof<RawMouseButtonTransition>
      keyboard_actions = KeyboardAction.None
      timestamp = 0L }

let wheel_event (delta: int64) =
    { kind = TimelineEventKind.Wheel
      dx = 0L
      dy = 0L
      wheel = delta
      button = Unchecked.defaultof<RawMouseButtonTransition>
      keyboard_actions = KeyboardAction.None
      timestamp = 0L }

let raw_mouse_button_event (transition: RawMouseButtonTransition) =
    { kind = TimelineEventKind.RawMouseButton
      dx = 0L
      dy = 0L
      wheel = 0L
      button = transition
      keyboard_actions = KeyboardAction.None
      timestamp = transition.timestamp }

let keyboard_actions_event (actions: KeyboardAction) (timestamp: int64) =
    { kind = TimelineEventKind.KeyboardActions
      dx = 0L
      dy = 0L
      wheel = 0L
      button = Unchecked.defaultof<RawMouseButtonTransition>
      keyboard_actions = actions
      timestamp = timestamp }

let enqueue_locked (event: TimelineEvent) (state: State) =
    if state.timeline_write - state.timeline_read >= int64 state.timeline_events.Length then
        Interlocked.Exchange(&state.timeline_overflow, 1) |> ignore
    else
        let index = int (state.timeline_write % int64 state.timeline_events.Length)
        state.timeline_events[index] <- event
        state.timeline_write <- state.timeline_write + 1L

let flush_movement_locked (state: State) =
    let struct (x, y) = unpack_mouse (Interlocked.Exchange(&state.mouse_xy, 0L))

    if x <> 0 || y <> 0 then
        enqueue_locked (movement_event (int64 x) (int64 y)) state

let add_boundary_event (event: TimelineEvent) (state: State) =
    Monitor.Enter state.timeline_gate

    try
        flush_movement_locked state
        enqueue_locked event state
    finally
        Monitor.Exit state.timeline_gate

    mark_work_available state

let add_raw_mouse_button_transition (transition: RawMouseButtonTransition) (state: State) =
    if transition.event <> RawMouseButtonEvent.None then
        add_boundary_event (raw_mouse_button_event transition) state

let add_wheel (delta: int) (state: State) =
    if delta <> 0 then
        add_boundary_event (wheel_event (int64 delta)) state

let add_keyboard_actions (actions: KeyboardAction) (timestamp: int64) (state: State) =
    if actions <> KeyboardAction.None then
        add_boundary_event (keyboard_actions_event actions timestamp) state

let drain_timeline (destination: TimelineEvent array) (state: State) =
    Monitor.Enter state.timeline_gate

    try
        let available = state.timeline_write - state.timeline_read

        if available > int64 state.timeline_events.Length then
            invalidOp "The input timeline contains more events than its fixed capacity."

        let requiredCapacity = int available + 1

        if destination.Length < requiredCapacity then
            invalidArg (nameof destination) $"The input timeline destination needs {requiredCapacity} entries."

        let mutable count = 0

        while state.timeline_read < state.timeline_write do
            let index = int (state.timeline_read % int64 state.timeline_events.Length)
            destination[count] <- state.timeline_events[index]
            count <- count + 1
            state.timeline_read <- state.timeline_read + 1L

        let struct (x, y) = unpack_mouse (Interlocked.Exchange(&state.mouse_xy, 0L))

        if x <> 0 || y <> 0 then
            destination[count] <- movement_event (int64 x) (int64 y)
            count <- count + 1

        let overflowed = Interlocked.Exchange(&state.timeline_overflow, 0) <> 0
        struct (count, overflowed)
    finally
        Monitor.Exit state.timeline_gate

let timeline_buffer () =
    Array.zeroCreate<TimelineEvent> (TIMELINE_EVENT_CAPACITY + 1)

let timeline_pending (state: State) =
    Volatile.Read(&state.timeline_read) < Volatile.Read(&state.timeline_write)

let discard_pointer_input (state: State) =
    Monitor.Enter state.timeline_gate

    try
        Interlocked.Exchange(&state.mouse_xy, 0L) |> ignore

        let mutable source = state.timeline_read
        let mutable destination = state.timeline_read

        while source < state.timeline_write do
            let sourceIndex = int (source % int64 state.timeline_events.Length)
            let event = state.timeline_events[sourceIndex]

            if
                event.kind <> TimelineEventKind.Movement
                && event.kind <> TimelineEventKind.Wheel
            then
                let destinationIndex = int (destination % int64 state.timeline_events.Length)
                state.timeline_events[destinationIndex] <- event
                destination <- destination + 1L

            source <- source + 1L

        state.timeline_write <- destination
    finally
        Monitor.Exit state.timeline_gate

let exit_reason (state: State) = Volatile.Read(&state.exit_reason)

let work_revision (state: State) =
    WorkRevision(Volatile.Read(&state.work_revision))

let work_pending_since (WorkRevision observed: WorkRevision) (state: State) =
    Option.isSome (exit_reason state)
    || Volatile.Read(&state.work_revision) <> observed
    || timeline_pending state
    || Volatile.Read(&state.timeline_overflow) <> 0

let discard_transient_input (state: State) =
    Monitor.Enter state.timeline_gate

    try
        Interlocked.Exchange(&state.mouse_xy, 0L) |> ignore
        state.timeline_read <- state.timeline_write
        Interlocked.Exchange(&state.timeline_overflow, 0) |> ignore
    finally
        Monitor.Exit state.timeline_gate
