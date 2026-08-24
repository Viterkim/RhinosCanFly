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

    while FlyState.is_running state do
        if not movementActive && not inputReady then
            let remainingSeconds =
                max 0. (state.next_host_validation_at - clock.Elapsed.TotalSeconds)

            let timeoutMilliseconds = int (Math.Ceiling(remainingSeconds * 1000.))
            PlatformInput.wait_for_input_for timeoutMilliseconds

        RhinoApp.Wait()

        let frameTimestamp = Stopwatch.GetTimestamp()
        let frameSeconds = clock.Elapsed.TotalSeconds
        let observedRawRevision = InputAccumulator.work_revision rawInput
        let observedKeyboardRevision = PlatformInput.flight_keyboard_revision ()
        FlightControls.update_state frameSeconds rawInput state
        let mutable wheelChange = ViewChange.none
        let mutable mouseChange = ViewChange.none

        if not (FlyState.is_running state) then
            InputAccumulator.discard_transient_input rawInput
        else
            FlightControls.update_keyboard_navigation_input state
            let navigationChange = FlightCamera.update_navigation_mode rawInput state
            wheelChange <- FlightControls.apply_wheel_input rawInput state
            mouseChange <- FlightCamera.apply_mouse_input rawInput state
            mouseChange <- ViewChange.combine navigationChange mouseChange

        PlatformInput.acknowledge_raw_input_wake inputWake

        inputReady <-
            InputAccumulator.work_pending_since observedRawRevision rawInput
            || PlatformInput.flight_keyboard_revision () <> observedKeyboardRevision

        if FlyState.is_running state then
            let mutable viewChange = ViewChange.combine wheelChange mouseChange

            let input = FlightControls.read_movement state
            let requestedPivotDirection = FlightInput.key_pivot_direction input

            let movement =
                match state.key_pivot_input_state with
                | KeyPivotInputArmed -> input
                | WaitingForNeutralKeyPivotInput ->
                    match requestedPivotDirection with
                    | NoKeyPivot ->
                        state.key_pivot_input_state <- KeyPivotInputArmed
                        input
                    | KeyPivotLeft
                    | KeyPivotRight -> FlightInput.without_key_pivot input

            let now = frameSeconds
            let currentlyMoving = FlightInput.movement_active movement
            let movementStarting = currentlyMoving && not movementActive
            let pivotDirection = FlightInput.key_pivot_direction movement

            let pivotTargetChanged =
                pivotDirection <> NoKeyPivot && pivotDirection <> state.key_pivot_direction

            if pivotTargetChanged then
                state.key_pivot_target <-
                    FlightCamera.navigation_target state ViewNavigationMode.Pivot state.gumball_pivot_target

            state.key_pivot_direction <- pivotDirection

            if movementStarting then
                // The keyboard message which started movement has already been consumed by RhinoApp.Wait.
                // Queue one fresh pass so continuous movement cannot sit idle until host validation.
                PlatformInput.wake_flight_loop inputWake

            if currentlyMoving then
                let dt =
                    if movementStarting then
                        let changedAt = PlatformInput.flight_keyboard_change_timestamp ()
                        let elapsedTicks = frameTimestamp - changedAt

                        if changedAt > 0L && elapsedTicks >= 0L then
                            min (float elapsedTicks / float Stopwatch.Frequency) MAXIMUM_FRAME_DELTA_SECONDS
                        else
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
                if movementStarting || pivotTargetChanged then
                    // Target acquisition time is not movement time and must not become a later jump.
                    clock.Elapsed.TotalSeconds
                else
                    now

            movementActive <- currentlyMoving

            FlightCamera.apply state viewChange
