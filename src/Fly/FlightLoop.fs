module RhinosCanFly.FlightLoop

open System
open System.Diagnostics
open Rhino

[<Literal>]
let MAXIMUM_FRAME_DELTA_SECONDS = 0.05

let run (inputWake: PlatformInput.RawInputWake) (rawInput: InputAccumulator.State) (state: FlyState) =
    let clock = Stopwatch.StartNew()
    let mutable previousFrameSeconds = clock.Elapsed.TotalSeconds
    let mutable movementActive = false
    let mutable inputReady = true
    let timeline = InputAccumulator.timeline_buffer ()

    let update_navigation_mode () =
        FlightCamera.update_navigation_mode state

    let discard_pointer_input () =
        state.wheel_remainder <- 0L
        InputAccumulator.discard_pointer_input rawInput

    while FlyState.is_running state do
        if not movementActive && not inputReady then
            let remainingSeconds =
                max 0. (state.next_host_validation_at - clock.Elapsed.TotalSeconds)

            let timeoutMilliseconds = int (Math.Ceiling(remainingSeconds * 1000.))
            PlatformInput.wait_for_input_for timeoutMilliseconds

        RhinoApp.Wait()

        let frameSeconds = clock.Elapsed.TotalSeconds
        let observedRawRevision = InputAccumulator.work_revision rawInput
        let observedKeyboardRevision = PlatformInput.flight_keyboard_revision ()
        FlightControls.update_state frameSeconds rawInput state

        if not (FlyState.is_running state) then
            PlatformInput.allow_keyboard_passthrough ()
            InputAccumulator.discard_transient_input rawInput
        else
            if update_navigation_mode () then
                discard_pointer_input ()

            let struct (timelineCount, timelineOverflowed) =
                InputAccumulator.drain_timeline timeline rawInput

            if timelineOverflowed then
                FlyState.request_exit (SessionFailure "The input timeline overflowed.") state

            let mutable timelineIndex = 0
            let mutable acceptPointerInput = true

            let apply_pointer_barrier () =
                acceptPointerInput <- false
                discard_pointer_input ()

            while FlyState.is_running state && timelineIndex < timelineCount do
                let event = timeline[timelineIndex]

                match event.kind with
                | InputAccumulator.TimelineEventKind.Movement when acceptPointerInput ->
                    FlightCamera.apply_mouse_delta event.dx event.dy state
                    |> FlightCamera.apply state
                | InputAccumulator.TimelineEventKind.Wheel when acceptPointerInput ->
                    FlightControls.apply_wheel_delta event.wheel state |> FlightCamera.apply state
                | InputAccumulator.TimelineEventKind.RawMouseButton ->
                    let effect = FlightControls.apply_raw_mouse_button_transition event.button state

                    if FlyState.is_running state then
                        FlightCamera.apply state effect.view_change

                        let navigationChanged = update_navigation_mode ()

                        if effect.pointer_barrier || navigationChanged then
                            apply_pointer_barrier ()
                | InputAccumulator.TimelineEventKind.KeyboardActions ->
                    let effect = FlightControls.apply_keyboard_actions event.keyboard_actions state

                    if FlyState.is_running state then
                        FlightCamera.apply state effect.view_change

                        let navigationChanged = update_navigation_mode ()

                        if effect.pointer_barrier || navigationChanged then
                            apply_pointer_barrier ()
                | InputAccumulator.TimelineEventKind.Movement
                | InputAccumulator.TimelineEventKind.Wheel -> ()
                | _ -> failwith "The input timeline contains an unknown event."

                timelineIndex <- timelineIndex + 1

            if not (FlyState.is_running state) then
                PlatformInput.allow_keyboard_passthrough ()
                InputAccumulator.discard_transient_input rawInput

        PlatformInput.acknowledge_raw_input_wake inputWake

        inputReady <-
            InputAccumulator.work_pending_since observedRawRevision rawInput
            || PlatformInput.flight_keyboard_revision () <> observedKeyboardRevision

        if FlyState.is_running state then
            let mutable viewChange = ViewChange.none

            let input = FlightControls.read_movement state
            let requestedPivotKeysDown = input.key_pivot_left || input.key_pivot_right

            let movement =
                match state.key_pivot_input_state with
                | WaitingForNeutralKeyPivotInput ->
                    if requestedPivotKeysDown then
                        FlightInput.without_key_pivot input
                    else
                        state.key_pivot_input_state <- KeyPivotInputArmed
                        input
                | KeyPivotInputArmed
                | KeyPivotInputActive -> input

            let now = frameSeconds
            let currentlyMoving = FlightInput.movement_active movement
            let movementStarting = currentlyMoving && not movementActive
            let pivotKeysDown = movement.key_pivot_left || movement.key_pivot_right
            let pivotDirectionActive = movement.key_pivot_left <> movement.key_pivot_right

            let pivotTargetStarting =
                pivotDirectionActive && state.key_pivot_input_state = KeyPivotInputArmed

            if pivotTargetStarting then
                state.key_pivot_target <-
                    FlightCamera.navigation_target state ViewNavigationMode.Pivot state.gumball_pivot_target

                discard_pointer_input ()

                state.key_pivot_input_state <- KeyPivotInputActive
            elif not pivotKeysDown && state.key_pivot_input_state = KeyPivotInputActive then
                state.key_pivot_input_state <- KeyPivotInputArmed

            if currentlyMoving then
                let dt =
                    if movementStarting then
                        0.
                    else
                        min (now - previousFrameSeconds) MAXIMUM_FRAME_DELTA_SECONDS

                let previousCamera = state.camera
                let parallelView = state.config.movement.parallel_view

                let parallelFlight = state.projection = ViewProjectionKind.Parallel

                let verticalSpeedMultiplier =
                    if parallelFlight then
                        parallelView.up_down_multiplier
                    else
                        state.config.movement.vertical_speed_multiplier

                let movementStep =
                    Movement.step
                        state.config.movement
                        verticalSpeedMultiplier
                        movement
                        state.key_pivot_target
                        dt
                        previousCamera

                let nextCamera = movementStep.camera

                let parallelMagnification =
                    FlightCamera.parallel_magnification_factor state movementStep.forward_distance

                if not (CameraState.valid nextCamera) then
                    state.restore_camera_on_exit <- true
                    failwith "Movement produced an invalid camera state."

                match state.active_mouse_navigation with
                | MousePivot pivotCenter ->
                    let translatedCenter = pivotCenter + movementStep.translation

                    state.active_mouse_navigation <-
                        MousePivot(
                            Movement.orbit_point state.key_pivot_target movementStep.key_pivot_angle translatedCenter
                        )
                | MousePan(panTarget, unitsPerRadian) ->
                    let translatedTarget = panTarget + movementStep.translation

                    state.active_mouse_navigation <-
                        MousePan(
                            Movement.orbit_point state.key_pivot_target movementStep.key_pivot_angle translatedTarget,
                            unitsPerRadian
                        )
                | MouseLook -> ()

                state.camera <- nextCamera

                viewChange <-
                    ViewChange.combine
                        viewChange
                        { camera_changed = nextCamera <> previousCamera
                          parallel_magnification = parallelMagnification }

            previousFrameSeconds <-
                if movementStarting || pivotTargetStarting then
                    // Don't count target lookup time as movement or the next frame jumps.
                    clock.Elapsed.TotalSeconds
                else
                    now

            movementActive <- currentlyMoving

            FlightCamera.apply state viewChange

            if
                movementStarting
                && not viewChange.camera_changed
                && viewChange.parallel_magnification = 1.
            then
                // If the first step is zero there is no redraw to wake the loop.
                PlatformInput.wake_flight_loop inputWake
