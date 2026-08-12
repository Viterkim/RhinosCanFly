module RhinosCanFly.FlightLoop

open System
open System.Diagnostics
open Rhino

[<Literal>]
let maximum_frame_delta_seconds = 0.05

let stationary_input_watchdog = TimeSpan.FromMilliseconds 75.

let run (inputWake: PlatformInput.RawInputWake) (rawInput: InputAccumulator.State) (state: FlyState) =
    let clock = Stopwatch.StartNew()
    let mutable previousFrame = clock.Elapsed.TotalSeconds
    let mutable movementActive = false
    let mutable rawInputReady = false

    while FlyState.is_running state do
        PlatformInput.record_flight_loop_iteration ()

        if not movementActive && not rawInputReady then
            PlatformInput.wait_for_raw_input inputWake stationary_input_watchdog

        let waitStarted = Stopwatch.GetTimestamp()
        RhinoApp.Wait()
        PlatformInput.record_rhino_wait (Stopwatch.GetTimestamp() - waitStarted)
        let observedRevision = InputAccumulator.work_revision rawInput
        FlightControls.update_state rawInput state
        let mutable mouseChange = NoCameraChange

        if not (FlyState.is_running state) then
            InputAccumulator.discard_transient_input rawInput
        else
            FlightControls.update_keyboard_navigation_input state
            FlightCamera.update_navigation_mode rawInput state
            mouseChange <- FlightCamera.apply_mouse_input rawInput state

        PlatformInput.acknowledge_raw_input_wake inputWake
        rawInputReady <- InputAccumulator.work_pending_since observedRevision rawInput

        if FlyState.is_running state then
            let mutable movementChanged =
                match mouseChange with
                | PositionChanged
                | PositionAndDirectionChanged -> true
                | DirectionChanged
                | NoCameraChange -> false

            let mutable directionChanged =
                match mouseChange with
                | DirectionChanged
                | PositionAndDirectionChanged -> true
                | PositionChanged
                | NoCameraChange -> false

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

            let now = clock.Elapsed.TotalSeconds
            let currentlyMoving = FlightInput.movement_active movement
            let pivotDirection = FlightInput.key_pivot_direction movement

            if pivotDirection <> NoKeyPivot && pivotDirection <> state.key_pivot_direction then
                state.key_pivot_target <- FlightCamera.navigation_target state.view state.gumball_pivot_target

            state.key_pivot_direction <- pivotDirection

            if movementActive && currentlyMoving then
                let dt = min (now - previousFrame) maximum_frame_delta_seconds
                let previousCamera = state.camera

                let nextCamera =
                    Movement.step state.config.movement movement state.key_pivot_target dt previousCamera

                if not (CameraState.valid nextCamera) then
                    state.restore_camera_on_exit <- true
                    failwith "Movement produced an invalid camera state."

                if nextCamera.position <> previousCamera.position then
                    movementChanged <- true

                if nextCamera.yaw <> previousCamera.yaw || nextCamera.pitch <> previousCamera.pitch then
                    directionChanged <- true

                state.camera <- nextCamera

            previousFrame <- now
            movementActive <- currentlyMoving

            // Keep this boring instead of matching a tuple. The F# tuple form
            // allocates System.Tuple<bool, bool> in this hot path.
            if movementChanged then
                if directionChanged then
                    FlightCamera.apply state PositionAndDirectionChanged
                else
                    FlightCamera.apply state PositionChanged
            elif directionChanged then
                FlightCamera.apply state DirectionChanged
