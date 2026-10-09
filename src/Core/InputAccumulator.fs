module RhinosCanFly.InputAccumulator

open System
open System.Diagnostics
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
    | UntiltView = 32768
    | DeferredExit = 65536

[<RequireQualifiedAccess>]
type TimelineEventKind =
    | Movement = 0
    | Wheel = 1
    | RawMouseButton = 2
    | KeyboardTransition = 3
    | BeginSession = 4
    | ExitKeepCamera = 5
    | ExitRestoreCamera = 6
    | ExitHeldRelease = 7
    | ExitCancelledEntry = 8

[<Struct>]
type TimelineEvent =
    { kind: TimelineEventKind
      timestamp: int64
      dx: int64
      dy: int64
      wheel: int64
      button: RawMouseButtonTransition
      key: int
      key_down: bool
      terminal: bool }

type State =
    { mutable mouse_xy: int64
      timeline_gate: obj
      timeline_events: TimelineEvent array
      mutable timeline_write: int64
      mutable timeline_read: int64
      mutable timeline_overflow: int
      mutable exit_reason: FlightExitReason option
      mutable escape_requested: bool
      mutable absolute_motion_warning: int
      mutable work_revision: int64 }

[<Struct>]
type WorkRevision = WorkRevision of int64

let event_exit
    (lifetime: FlightLifetime)
    (exit_buttons: MouseExitConfig)
    (actions: KeyboardAction)
    (button: RawMouseButtonEvent)
    =
    if int actions &&& int KeyboardAction.CancelAndRestore <> 0 then
        Some ExplicitRestoreCamera
    elif int actions &&& int KeyboardAction.Exit <> 0 then
        Some ExplicitKeepCamera
    else
        match button with
        | RawMouseButtonEvent.LeftUp when exit_buttons.left -> Some ExplicitKeepCamera
        | RawMouseButtonEvent.RightUp when lifetime = FlightLifetime.WhileRightMouseHeld -> Some RightMouseReleased
        | RawMouseButtonEvent.RightUp when exit_buttons.right -> Some ExplicitKeepCamera
        | RawMouseButtonEvent.MiddleUp when exit_buttons.middle -> Some ExplicitKeepCamera
        | RawMouseButtonEvent.Mouse4Up when exit_buttons.mouse4 -> Some ExplicitKeepCamera
        | RawMouseButtonEvent.Mouse5Up when exit_buttons.mouse5 -> Some ExplicitKeepCamera
        | _ -> None

let create_with_capacity (capacity: int) =
    { mouse_xy = 0L
      timeline_gate = obj ()
      timeline_events = Array.zeroCreate capacity
      timeline_write = 0L
      timeline_read = 0L
      timeline_overflow = 0
      exit_reason = None
      escape_requested = false
      absolute_motion_warning = 0
      work_revision = 0L }

let create () =
    create_with_capacity TIMELINE_EVENT_CAPACITY

let mark_work_available (state: State) =
    Interlocked.Increment(&state.work_revision) |> ignore

let observe_absolute_motion (state: State) =
    if Interlocked.CompareExchange(&state.absolute_motion_warning, 1, 0) = 0 then
        mark_work_available state
        true
    else
        false

let take_absolute_motion_warning (state: State) =
    // 0: unseen, 1: pending, 2: reported for this session.
    Interlocked.CompareExchange(&state.absolute_motion_warning, 2, 1) = 1

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
            let struct (current_x, current_y) = unpack_mouse current

            let next =
                pack_mouse (saturating_add current_x (int32 dx)) (saturating_add current_y (int32 dy))

            updated <- Interlocked.CompareExchange(&state.mouse_xy, next, current) = current

        mark_work_available state

let request_exit (reason: FlightExitReason) (state: State) =
    let previous = Interlocked.CompareExchange(&state.exit_reason, Some reason, None)

    if Option.isNone previous then
        mark_work_available state

let movement_event (dx: int64) (dy: int64) =
    { kind = TimelineEventKind.Movement
      timestamp = Stopwatch.GetTimestamp()
      dx = dx
      dy = dy
      wheel = 0L
      button = Unchecked.defaultof<RawMouseButtonTransition>
      key = 0
      key_down = false
      terminal = false }

let wheel_event (delta: int64) =
    { kind = TimelineEventKind.Wheel
      timestamp = 0L
      dx = 0L
      dy = 0L
      wheel = delta
      button = Unchecked.defaultof<RawMouseButtonTransition>
      key = 0
      key_down = false
      terminal = false }

let raw_mouse_button_event (transition: RawMouseButtonTransition) =
    { kind = TimelineEventKind.RawMouseButton
      timestamp = 0L
      dx = 0L
      dy = 0L
      wheel = 0L
      button = transition
      key = 0
      key_down = false
      terminal = false }

let enqueue_locked (event: TimelineEvent) (state: State) =
    if state.timeline_write - state.timeline_read >= int64 state.timeline_events.Length then
        Interlocked.Exchange(&state.timeline_overflow, 1) |> ignore
    else
        let index = int (state.timeline_write % int64 state.timeline_events.Length)

        state.timeline_events[index] <-
            if event.timestamp = 0L then
                { event with
                    timestamp = Stopwatch.GetTimestamp() }
            else
                event

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

let add_timed_mouse (timestamp: int64) (dx: int64) (dy: int64) (state: State) =
    if dx <> 0L || dy <> 0L then
        Monitor.Enter state.timeline_gate

        try
            flush_movement_locked state

            enqueue_locked
                { movement_event dx dy with
                    timestamp = timestamp }
                state
        finally
            Monitor.Exit state.timeline_gate

        mark_work_available state

let add_raw_mouse_button_transition (transition: RawMouseButtonTransition) (state: State) =
    if transition.event <> RawMouseButtonEvent.None then
        add_boundary_event (raw_mouse_button_event transition) state

let add_wheel (delta: int) (state: State) =
    if delta <> 0 then
        add_boundary_event (wheel_event (int64 delta)) state

let add_keyboard_transition_with_terminal (timestamp: int64) (key: int) (down: bool) (terminal: bool) (state: State) =
    add_boundary_event
        { kind = TimelineEventKind.KeyboardTransition
          timestamp = timestamp
          dx = 0L
          dy = 0L
          wheel = 0L
          button = Unchecked.defaultof<RawMouseButtonTransition>
          key = key
          key_down = down
          terminal = terminal }
        state

let add_keyboard_transition (timestamp: int64) (key: int) (down: bool) (state: State) =
    add_keyboard_transition_with_terminal timestamp key down false state

let drain_timeline (destination: TimelineEvent array) (state: State) =
    Monitor.Enter state.timeline_gate

    try
        let available = state.timeline_write - state.timeline_read

        if available > int64 state.timeline_events.Length then
            invalidOp "The input timeline contains more events than its fixed capacity."

        let required_capacity = int available + 1

        if destination.Length < required_capacity then
            invalidArg (nameof destination) $"The input timeline destination needs {required_capacity} entries."

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

let timeline_buffer_for (state: State) =
    Array.zeroCreate<TimelineEvent> (state.timeline_events.Length + 1)

let timeline_pending (state: State) =
    Volatile.Read(&state.timeline_read) < Volatile.Read(&state.timeline_write)

let work_pending (state: State) =
    Volatile.Read(&state.escape_requested)
    || Option.isSome (Volatile.Read(&state.exit_reason))
    || Volatile.Read(&state.mouse_xy) <> 0L
    || Volatile.Read(&state.absolute_motion_warning) = 1
    || timeline_pending state
    || Volatile.Read(&state.timeline_overflow) <> 0

let discard_pointer_input (state: State) =
    Monitor.Enter state.timeline_gate

    try
        Interlocked.Exchange(&state.mouse_xy, 0L) |> ignore

        let mutable source = state.timeline_read
        let mutable destination = state.timeline_read

        while source < state.timeline_write do
            let source_index = int (source % int64 state.timeline_events.Length)
            let event = state.timeline_events[source_index]

            if
                event.kind <> TimelineEventKind.Movement
                && event.kind <> TimelineEventKind.Wheel
            then
                let destination_index = int (destination % int64 state.timeline_events.Length)
                state.timeline_events[destination_index] <- event
                destination <- destination + 1L

            source <- source + 1L

        state.timeline_write <- destination
    finally
        Monitor.Exit state.timeline_gate

let exit_reason (state: State) = Volatile.Read(&state.exit_reason)

let work_revision (state: State) =
    WorkRevision(Volatile.Read(&state.work_revision))

let work_pending_since (WorkRevision observed: WorkRevision) (state: State) =
    work_pending state || Volatile.Read(&state.work_revision) <> observed

let discard_transient_input (state: State) =
    Monitor.Enter state.timeline_gate

    try
        Interlocked.Exchange(&state.mouse_xy, 0L) |> ignore
        state.timeline_read <- state.timeline_write
        Interlocked.Exchange(&state.timeline_overflow, 0) |> ignore
    finally
        Monitor.Exit state.timeline_gate
