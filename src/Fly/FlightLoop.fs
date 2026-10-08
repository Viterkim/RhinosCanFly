module RhinosCanFly.FlightLoop

open System
open System.Diagnostics
open Rhino

[<Literal>]
let MAXIMUM_FRAME_DELTA_SECONDS = 0.05

let movement_interval (previous: int64) (frame: int64) (boundary: int64) (pauses: ResizeArray<struct (int64 * int64)>) =
    let maximum_ticks = int64 (MAXIMUM_FRAME_DELTA_SECONDS * float Stopwatch.Frequency)
    let start = max previous (frame - maximum_ticks)
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

let create
    (validate_input: unit -> Result<bool, string>)
    (acknowledge_input: unit -> unit)
    (raw_input: InputAccumulator.State)
    (state: FlyState)
    =
    let clock = Stopwatch.StartNew()
    let mutable movement_clock = Stopwatch.GetTimestamp()
    let mutable movement_active = false
    let mutable input_ready = true
    let mutable observed_raw_revision = InputAccumulator.work_revision raw_input
    let mutable observed_keyboard_revision = PlatformFlightKeyboard.revision ()
    let timeline = InputAccumulator.timeline_buffer ()
    let movement_times = Array.zeroCreate<int64> timeline.Length
    let mutable batch_end = movement_clock
    let mutable redraw_required = false
    let target_work = ResizeArray<struct (int64 * int64)>(4 * timeline.Length + 4)

    let record_target_work (started: int64) =
        let finished = Stopwatch.GetTimestamp()
        target_work.Add(struct (started, finished))

    let update_navigation_mode () =
        let started = Stopwatch.GetTimestamp()
        let changed = FlightCamera.update_navigation_mode state

        if changed then
            match state.active_mouse_navigation with
            | MousePivot _ -> record_target_work started
            | MousePan _ -> record_target_work started
            | MouseLook -> ()

        changed

    let discard_pointer_input () =
        FlightCamera.rebase_active_pivot state
        state.wheel_remainder <- 0L
        InputAccumulator.discard_pointer_input raw_input

    let reset_movement_clock () =
        movement_clock <- max movement_clock (Stopwatch.GetTimestamp())

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
                let started = Stopwatch.GetTimestamp()
                let target = FlightCamera.navigation_target state ViewNavigationMode.Pivot
                record_target_work started

                if FlyState.can_write_camera state then
                    state.key_pivot_target <- target
                    FlightCamera.rebase_active_pivot state
                    state.key_pivot_input_state <- KeyPivotInputActive
            elif not pivot_keys_down then
                state.key_pivot_input_state <- KeyPivotInputArmed

            if FlyState.is_running state then
                let change = FlightCamera.apply_movement movement seconds state
                redraw_required <- FlightCamera.write_view state change || redraw_required

    let step () =
        input_ready <- false
        let frame_seconds = clock.Elapsed.TotalSeconds
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

        FlightControls.update_state frame_seconds raw_input state

        if not (FlyState.is_running state) then
            PlatformFlightKeyboard.allow_passthrough ()
            InputAccumulator.discard_transient_input raw_input
        else
            if update_navigation_mode () then
                FlightCamera.rebase_active_pivot state

            let struct (frame, movement_end) = PlatformFlightKeyboard.movement_boundary ()
            batch_end <- frame

            let struct (timeline_count, timeline_overflowed) =
                InputAccumulator.drain_timeline timeline raw_input

            movement_boundaries movement_end timeline timeline_count movement_times

            if timeline_overflowed then
                FlyState.request_exit (SessionFailure "The input timeline overflowed.") state

            let mutable timeline_index = 0
            let mutable discard_remaining_pointer = false

            while FlyState.is_running state && timeline_index < timeline_count do
                let event = timeline[timeline_index]
                advance_movement movement_times[timeline_index]

                match event.kind with
                | InputAccumulator.TimelineEventKind.Movement when not discard_remaining_pointer ->
                    let change = FlightCamera.apply_mouse_delta event.dx event.dy state
                    redraw_required <- FlightCamera.write_view state change || redraw_required
                | InputAccumulator.TimelineEventKind.Wheel when not discard_remaining_pointer ->
                    let change = FlightControls.apply_wheel_delta event.wheel state
                    redraw_required <- FlightCamera.write_view state change || redraw_required
                | InputAccumulator.TimelineEventKind.RawMouseButton ->
                    let effect = FlightControls.apply_raw_mouse_button_transition event.button state

                    if FlyState.is_running state then
                        redraw_required <- FlightCamera.write_view state effect.view_change || redraw_required
                        let navigation_changed = update_navigation_mode ()

                        if effect.pointer_rebase_required then
                            discard_pointer_input ()
                            discard_remaining_pointer <- true
                            reset_movement_clock ()
                        elif navigation_changed then
                            FlightCamera.rebase_active_pivot state
                | InputAccumulator.TimelineEventKind.KeyboardTransition ->
                    let actions =
                        PlatformFlightKeyboard.apply_keyboard_transition event.key event.key_down

                    let effect = FlightControls.apply_keyboard_actions actions state

                    if FlyState.is_running state then
                        redraw_required <- FlightCamera.write_view state effect.view_change || redraw_required
                        let navigation_changed = update_navigation_mode ()

                        if effect.pointer_rebase_required then
                            discard_pointer_input ()
                            discard_remaining_pointer <- true
                            reset_movement_clock ()
                        elif navigation_changed then
                            FlightCamera.rebase_active_pivot state
                | InputAccumulator.TimelineEventKind.Movement
                | InputAccumulator.TimelineEventKind.Wheel -> ()
                | _ -> failwith "The input timeline contains an unknown event."

                timeline_index <- timeline_index + 1

            if FlyState.is_running state then
                advance_movement movement_end
                movement_active <- FlightControls.read_movement state |> FlightInput.movement_active
            else
                PlatformFlightKeyboard.allow_passthrough ()
                InputAccumulator.discard_transient_input raw_input

        acknowledge_input ()

        if FlyState.is_running state && redraw_required then
            FlightCamera.redraw state

    { step = step
      work_pending =
        fun () ->
            movement_active
            || input_ready
            || InputAccumulator.work_pending_since observed_raw_revision raw_input
            || PlatformFlightKeyboard.revision () <> observed_keyboard_revision
      wait_timeout =
        fun () ->
            let remaining_seconds =
                max 0. (state.next_host_validation_at - clock.Elapsed.TotalSeconds)

            int (Math.Ceiling(remaining_seconds * 1000.)) }
