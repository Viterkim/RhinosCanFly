module RhinosCanFly.FlightLoop

open System
open System.Diagnostics
open Rhino

[<Literal>]
let MAXIMUM_FRAME_DELTA_SECONDS = 0.05

let run
    (input_wake: PlatformInputWake.State)
    (raw_input: InputAccumulator.State)
    (raw: PlatformRawInput.Session)
    (state: FlyState)
    =
    let clock = Stopwatch.StartNew()
    let mutable previous_frame_seconds = clock.Elapsed.TotalSeconds
    let mutable movement_active = false
    let mutable input_ready = true
    let mutable observed_raw_revision = InputAccumulator.work_revision raw_input
    let mutable observed_keyboard_revision = PlatformFlightKeyboard.revision ()
    let timeline = InputAccumulator.timeline_buffer ()

    let discard_pointer_input () =
        FlightCamera.rebase_active_pivot state
        state.wheel_remainder <- 0L
        InputAccumulator.discard_pointer_input raw_input

    let step () =
        input_ready <- false
        let frame_seconds = clock.Elapsed.TotalSeconds
        let mutable reset_movement_clock = false
        let mutable redraw_required = false
        observed_raw_revision <- InputAccumulator.work_revision raw_input
        observed_keyboard_revision <- PlatformFlightKeyboard.revision ()

        if frame_seconds >= state.next_host_validation_at then
            match PlatformRawInput.registration_is_current raw with
            | Ok true -> ()
            | Ok false -> FlyState.request_exit (SessionFailure "Raw mouse registration changed owners.") state
            | Error error -> FlyState.request_exit (SessionFailure error) state

        FlightControls.update_state frame_seconds raw_input state

        if not (FlyState.is_running state) then
            PlatformFlightKeyboard.allow_passthrough ()
            InputAccumulator.discard_transient_input raw_input
        else
            if FlightCamera.update_navigation_mode state then
                FlightCamera.rebase_active_pivot state
                reset_movement_clock <- true

            let struct (timeline_count, timeline_overflowed) =
                InputAccumulator.drain_timeline timeline raw_input

            if timeline_overflowed then
                FlyState.request_exit (SessionFailure "The input timeline overflowed.") state

            let mutable timeline_index = 0
            let mutable discard_remaining_pointer = false

            while FlyState.is_running state && timeline_index < timeline_count do
                let event = timeline[timeline_index]

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

                        let navigation_changed = FlightCamera.update_navigation_mode state

                        if effect.pointer_rebase_required then
                            discard_pointer_input ()
                            discard_remaining_pointer <- true
                            reset_movement_clock <- true
                        elif navigation_changed then
                            FlightCamera.rebase_active_pivot state
                            reset_movement_clock <- true
                | InputAccumulator.TimelineEventKind.KeyboardTransition ->
                    let actions =
                        PlatformFlightKeyboard.apply_keyboard_transition event.key event.key_down

                    let effect = FlightControls.apply_keyboard_actions actions state

                    if FlyState.is_running state then
                        redraw_required <- FlightCamera.write_view state effect.view_change || redraw_required

                        let navigation_changed = FlightCamera.update_navigation_mode state

                        if effect.pointer_rebase_required then
                            discard_pointer_input ()
                            discard_remaining_pointer <- true
                            reset_movement_clock <- true
                        elif navigation_changed then
                            FlightCamera.rebase_active_pivot state
                            reset_movement_clock <- true
                | InputAccumulator.TimelineEventKind.Movement
                | InputAccumulator.TimelineEventKind.Wheel -> ()
                | _ -> failwith "The input timeline contains an unknown event."

                timeline_index <- timeline_index + 1

            if not (FlyState.is_running state) then
                PlatformFlightKeyboard.allow_passthrough ()
                InputAccumulator.discard_transient_input raw_input

        PlatformInputWake.acknowledge input_wake

        if FlyState.is_running state then
            let mutable view_change = ViewChange.none

            let movement = FlightControls.read_movement state

            let now = frame_seconds
            let currently_moving = FlightInput.movement_active movement
            let movement_starting = currently_moving && not movement_active
            let pivot_keys_down = movement.key_pivot_left || movement.key_pivot_right
            let pivot_direction_active = movement.key_pivot_left <> movement.key_pivot_right

            let pivot_target_starting =
                pivot_direction_active && state.key_pivot_input_state = KeyPivotInputArmed

            if pivot_target_starting then
                let target = FlightCamera.navigation_target state ViewNavigationMode.Pivot

                if FlyState.can_write_camera state then
                    state.key_pivot_target <- target
                    discard_pointer_input ()
                    state.key_pivot_input_state <- KeyPivotInputActive
            elif not pivot_keys_down && state.key_pivot_input_state = KeyPivotInputActive then
                state.key_pivot_input_state <- KeyPivotInputArmed

            if currently_moving && FlyState.can_write_camera state then
                let dt =
                    if movement_starting then
                        0.
                    else
                        min (now - previous_frame_seconds) MAXIMUM_FRAME_DELTA_SECONDS

                let previous_camera = state.camera
                let parallel_projection = state.config.movement.parallel_projection

                let parallel_flight = state.projection = ViewProjectionKind.Parallel

                let vertical_speed_multiplier =
                    if parallel_flight then
                        parallel_projection.up_down_multiplier
                    else
                        state.config.movement.vertical_speed_multiplier

                let movement_step =
                    Movement.step
                        state.config.movement
                        vertical_speed_multiplier
                        state.walking_plane
                        movement
                        state.key_pivot_target
                        dt
                        previous_camera

                let next_camera = movement_step.camera

                let parallel_magnification =
                    FlightCamera.parallel_magnification_factor state movement_step.forward_distance

                if not (CameraState.valid next_camera) then
                    state.restore_camera_on_exit <- true
                    failwith "Movement produced an invalid camera state."

                match state.active_mouse_navigation with
                | MousePivot drag ->
                    PivotOrbit.transform_for_flight_movement
                        movement_step.translation
                        state.key_pivot_target
                        movement_step.key_pivot_angle
                        drag
                | MousePan(pan_target, units_per_radian) ->
                    let translated_target = pan_target + movement_step.translation

                    state.active_mouse_navigation <-
                        MousePan(
                            Movement.orbit_point state.key_pivot_target movement_step.key_pivot_angle translated_target,
                            units_per_radian
                        )
                | MouseLook -> ()

                state.camera <- next_camera

                view_change <-
                    ViewChange.combine
                        view_change
                        { camera_changed = next_camera <> previous_camera
                          parallel_magnification = parallel_magnification }

            previous_frame_seconds <-
                if movement_starting || pivot_target_starting || reset_movement_clock then
                    // Don't count target or projection work as movement or the next frame jumps.
                    clock.Elapsed.TotalSeconds
                else
                    now

            movement_active <- currently_moving

            redraw_required <- FlightCamera.write_view state view_change || redraw_required

            if redraw_required then
                FlightCamera.redraw state

            if
                movement_starting
                && not view_change.camera_changed
                && view_change.parallel_magnification = 1.
            then
                // If the first step is zero there is no redraw to wake the loop.
                PlatformInputWake.signal input_wake

    NavigationLoop.run
        (fun () -> FlyState.is_running state)
        (fun () ->
            movement_active
            || input_ready
            || InputAccumulator.work_pending_since observed_raw_revision raw_input
            || PlatformFlightKeyboard.revision () <> observed_keyboard_revision)
        (fun () ->
            let remaining_seconds =
                max 0. (state.next_host_validation_at - clock.Elapsed.TotalSeconds)

            int (Math.Ceiling(remaining_seconds * 1000.)))
        step
