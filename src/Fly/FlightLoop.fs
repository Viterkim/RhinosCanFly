module RhinosCanFly.FlightLoop

open System
open System.Diagnostics
open Rhino

[<Literal>]
let MAXIMUM_FRAME_DELTA_SECONDS = 0.05

[<Literal>]
let LONG_PAUSE_SECONDS = 1.

let movement_is_actionable (ordered: bool) (held: bool) (previous: int64) (frontier: int64) =
    held && (not ordered || frontier > previous)

let apply_ordered_exit (publish: unit -> unit) (kind: InputAccumulator.TimelineEventKind) (state: FlyState) =
    let reason =
        match kind with
        | InputAccumulator.TimelineEventKind.ExitKeepCamera -> FlightControls.explicit_exit_reason state
        | InputAccumulator.TimelineEventKind.ExitRestoreCamera -> ExplicitRestoreCamera
        | InputAccumulator.TimelineEventKind.ExitHeldRelease -> RightMouseReleased
        | InputAccumulator.TimelineEventKind.ExitCancelledEntry -> EntryCancelled
        | _ -> invalidOp "The replay record is not an ordered exit."

    publish ()
    FlyState.request_exit reason state

let movement_interval (previous: int64) (frame: int64) (boundary: int64) (pauses: ResizeArray<struct (int64 * int64)>) =
    let start = previous
    let finish = max start (min frame boundary)
    let mutable paused = 0L

    for index = 0 to pauses.Count - 1 do
        let struct (pause_start, pause_end) = pauses[index]
        paused <- paused + max 0L (min finish pause_end - max start pause_start)

    struct (finish, float (finish - start - paused) / float Stopwatch.Frequency)

let movement_boundaries
    (frame: int64)
    (events: InputAccumulator.TimelineEvent array)
    (count: int)
    (destination: int64 array)
    =
    // A flush ahead of an older queued key must leave that key's held time available.
    let mutable boundary = frame

    for index = count - 1 downto 0 do
        boundary <- min boundary events[index].timestamp
        destination[index] <- boundary

type State =
    { step: unit -> unit
      work_pending: unit -> bool
      wait_timeout: unit -> int }

type Host =
    { timestamp: unit -> int64
      elapsed_seconds: unit -> float
      movement_boundary: unit -> struct (int64 * int64)
      inspect: float -> unit
      publish: ViewChange -> bool
      update_navigation: unit -> bool
      redraw: unit -> unit
      pivot_target: unit -> Rhino.Geometry.Point3d }

let create_core
    (host: Host)
    (validate_input: unit -> Result<bool, string>)
    (acknowledge_input: unit -> unit)
    (raw_input: InputAccumulator.State)
    (state: FlyState)
    =
    let mutable movement_clock = host.timestamp ()
    let mutable movement_active = false
    let mutable input_ready = true
    let mutable observed_raw_revision = InputAccumulator.work_revision raw_input
    let mutable observed_keyboard_revision = PlatformFlightKeyboard.revision ()
    let timeline = InputAccumulator.timeline_buffer_for raw_input
    let movement_times = Array.zeroCreate<int64> timeline.Length
    let mutable batch_end = movement_clock
    let mutable redraw_required = false
    let mutable pending_view_change = ViewChange.none
    let target_work = ResizeArray<struct (int64 * int64)>(4 * timeline.Length + 4)

    let record_target_work (started: int64) =
        let finished = host.timestamp ()
        target_work.Add(struct (started, finished))

    let publish_view () =
        redraw_required <- host.publish pending_view_change || redraw_required
        pending_view_change <- ViewChange.none

    let accumulate_view (change: ViewChange) =
        pending_view_change <- ViewChange.combine pending_view_change change

    let update_navigation_mode () =
        publish_view ()
        let started = host.timestamp ()
        let changed = host.update_navigation ()

        if changed then
            match state.active_mouse_navigation with
            | MousePivot _ -> record_target_work started
            | MousePan _ -> record_target_work started
            | MouseLook -> ()

        changed

    let discard_pointer_input () =
        FlightCamera.rebase_active_pivot state
        state.wheel_remainder <- 0L
        PlatformFlightKeyboard.discard_pointer_input ()
        InputAccumulator.discard_pointer_input raw_input

    let reset_movement_clock () =
        movement_clock <- max movement_clock (host.timestamp ())

    let advance_movement (boundary: int64) =
        let struct (next_clock, seconds) =
            movement_interval movement_clock batch_end boundary target_work

        movement_clock <- next_clock

        let mutable completed = 0

        while completed < target_work.Count
              && (let struct (_, finished) = target_work[completed] in finished <= next_clock) do
            completed <- completed + 1

        if completed <> 0 then
            target_work.RemoveRange(0, completed)

        if FlyState.can_write_camera state then
            let movement = FlightControls.read_movement state
            let pivot_keys_down = movement.key_pivot_left || movement.key_pivot_right
            let pivot_direction_active = movement.key_pivot_left <> movement.key_pivot_right

            if pivot_direction_active && state.key_pivot_input_state = KeyPivotInputArmed then
                publish_view ()
                let started = host.timestamp ()
                let target = host.pivot_target ()
                record_target_work started

                if FlyState.can_write_camera state then
                    state.key_pivot_target <- target
                    FlightCamera.rebase_active_pivot state
                    state.key_pivot_input_state <- KeyPivotInputActive
            elif not pivot_keys_down then
                state.key_pivot_input_state <- KeyPivotInputArmed

            if FlyState.is_running state then
                let mutable remaining = seconds

                while remaining > 0. && FlyState.can_write_camera state do
                    let step = min MAXIMUM_FRAME_DELTA_SECONDS remaining
                    FlightCamera.apply_movement movement step state |> accumulate_view
                    remaining <- remaining - step

    let step () =
        input_ready <- false
        let frame_seconds = host.elapsed_seconds ()
        redraw_required <- false
        observed_raw_revision <- InputAccumulator.work_revision raw_input
        observed_keyboard_revision <- PlatformFlightKeyboard.revision ()

        if InputAccumulator.take_absolute_motion_warning raw_input then
            try
                RhinoApp.WriteLine
                    "RhinosCanFly: absolute-position mouse motion is unsupported. Use a relative mouse; buttons and wheel still work."
            with error ->
                Debug.WriteLine $"RhinosCanFly absolute-input warning output failed: {error.Message}"

        if frame_seconds >= state.next_host_validation_at then
            match validate_input () with
            | Ok true -> ()
            | Ok false -> FlyState.request_exit (SessionFailure "The navigation input session lost ownership.") state
            | Error error -> FlyState.request_exit (SessionFailure error) state

        host.inspect frame_seconds

        if not (FlyState.is_running state) then
            PlatformFlightKeyboard.allow_passthrough ()
            InputAccumulator.discard_transient_input raw_input
        else
            if update_navigation_mode () then
                FlightCamera.rebase_active_pivot state

            let struct (frame, movement_end) = host.movement_boundary ()
            batch_end <- frame

            let pause_cutoff =
                if float (frame - movement_clock) / float Stopwatch.Frequency > LONG_PAUSE_SECONDS then
                    frame
                else
                    0L

            if pause_cutoff <> 0L then
                movement_clock <- pause_cutoff
                PlatformFlightKeyboard.record_long_pause ()

            let struct (timeline_count, timeline_overflowed) =
                InputAccumulator.drain_timeline timeline raw_input

            movement_boundaries movement_end timeline timeline_count movement_times

            if timeline_overflowed then
                FlyState.request_exit (SessionFailure "The input timeline overflowed.") state

            let mutable timeline_index = 0
            let mutable discard_remaining_pointer = false

            while FlyState.is_running state && timeline_index < timeline_count do
                let event = timeline[timeline_index]

                if event.kind = InputAccumulator.TimelineEventKind.BeginSession then
                    movement_clock <- max pause_cutoff event.timestamp
                else
                    advance_movement movement_times[timeline_index]

                match event.kind with
                | InputAccumulator.TimelineEventKind.Movement when not discard_remaining_pointer ->
                    let change = FlightCamera.apply_mouse_delta event.dx event.dy state
                    accumulate_view change
                | InputAccumulator.TimelineEventKind.Wheel when not discard_remaining_pointer ->
                    let change = FlightControls.apply_wheel_delta event.wheel state
                    accumulate_view change
                | InputAccumulator.TimelineEventKind.RawMouseButton ->
                    publish_view ()

                    let effect =
                        FlightControls.apply_raw_mouse_button_transition_with_terminal event.terminal event.button state

                    if FlyState.is_running state then
                        redraw_required <- host.publish effect.view_change || redraw_required
                        let navigation_changed = not event.terminal && update_navigation_mode ()

                        if effect.pointer_rebase_required then
                            discard_pointer_input ()
                            discard_remaining_pointer <- true
                            reset_movement_clock ()
                        elif navigation_changed then
                            FlightCamera.rebase_active_pivot state
                | InputAccumulator.TimelineEventKind.KeyboardTransition ->
                    publish_view ()

                    let actions =
                        PlatformFlightKeyboard.apply_keyboard_transition event.key event.key_down

                    let terminal =
                        event.terminal
                        || FlightControls.has_keyboard_action actions InputAccumulator.KeyboardAction.DeferredExit

                    let effect =
                        if terminal then
                            InputEffect.none
                        else
                            FlightControls.apply_keyboard_actions actions state

                    if FlyState.is_running state then
                        redraw_required <- host.publish effect.view_change || redraw_required
                        let navigation_changed = not terminal && update_navigation_mode ()

                        if effect.pointer_rebase_required then
                            discard_pointer_input ()
                            discard_remaining_pointer <- true
                            reset_movement_clock ()
                        elif navigation_changed then
                            FlightCamera.rebase_active_pivot state
                | InputAccumulator.TimelineEventKind.Movement
                | InputAccumulator.TimelineEventKind.Wheel -> ()
                | InputAccumulator.TimelineEventKind.BeginSession ->
                    let navigation = state.config.bindings.mouse_navigation
                    state.keyboard_pivot_held <- FlightControls.is_optional_down navigation.pivot.hold
                    state.keyboard_pan_held <- FlightControls.is_optional_down navigation.pan.hold
                    update_navigation_mode () |> ignore
                | InputAccumulator.TimelineEventKind.ExitKeepCamera
                | InputAccumulator.TimelineEventKind.ExitRestoreCamera
                | InputAccumulator.TimelineEventKind.ExitHeldRelease
                | InputAccumulator.TimelineEventKind.ExitCancelledEntry ->
                    apply_ordered_exit publish_view event.kind state
                | _ -> failwith "The input timeline contains an unknown event."

                timeline_index <- timeline_index + 1

            if FlyState.is_running state then
                advance_movement movement_end
                movement_active <- FlightControls.read_movement state |> FlightInput.movement_active
            else
                PlatformFlightKeyboard.allow_passthrough ()
                InputAccumulator.discard_transient_input raw_input

        acknowledge_input ()
        publish_view ()

        if FlyState.is_running state && redraw_required then
            host.redraw ()

    { step = step
      work_pending =
        fun () ->
            let ordered = PlatformFlightKeyboard.ordered_exit_protocol ()

            let struct (_, frontier) =
                if ordered then
                    host.movement_boundary ()
                else
                    struct (movement_clock, movement_clock)

            movement_is_actionable ordered movement_active movement_clock frontier
            || input_ready
            || InputAccumulator.work_pending_since observed_raw_revision raw_input
            || PlatformFlightKeyboard.revision () <> observed_keyboard_revision
      wait_timeout =
        fun () ->
            let remaining_seconds =
                max 0. (state.next_host_validation_at - host.elapsed_seconds ())

            int (Math.Ceiling(remaining_seconds * 1000.)) }

let create
    (validate_input: unit -> Result<bool, string>)
    (acknowledge_input: unit -> unit)
    (raw_input: InputAccumulator.State)
    (state: FlyState)
    =
    let clock = Stopwatch.StartNew()

    let host =
        { timestamp = Stopwatch.GetTimestamp
          elapsed_seconds = fun () -> clock.Elapsed.TotalSeconds
          movement_boundary = PlatformFlightKeyboard.movement_boundary
          inspect = fun (seconds: float) -> FlightControls.update_state seconds raw_input state
          publish = FlightCamera.write_view state
          update_navigation = fun () -> FlightCamera.update_navigation_mode state
          redraw = fun () -> FlightCamera.redraw state
          pivot_target = fun () -> FlightCamera.navigation_target state ViewNavigationMode.Pivot }

    create_core host validate_input acknowledge_input raw_input state
