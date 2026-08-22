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
    let mutable rawInputReady = true

    while FlyState.is_running state do
        if not movementActive && not rawInputReady then
            let remainingSeconds =
                max 0. (state.next_host_validation_at - clock.Elapsed.TotalSeconds)

            let timeoutMilliseconds = int (Math.Ceiling(remainingSeconds * 1000.))
            PlatformInput.wait_for_input_for timeoutMilliseconds

        RhinoApp.Wait()

        let frameSeconds = clock.Elapsed.TotalSeconds
        let observedRevision = InputAccumulator.work_revision rawInput
        FlightControls.update_state frameSeconds rawInput state
        let mutable wheelChange = NoCameraChange
        let mutable mouseChange = NoCameraChange

        if not (FlyState.is_running state) then
            InputAccumulator.discard_transient_input rawInput
        else
            FlightControls.update_keyboard_navigation_input state
            FlightCamera.update_navigation_mode rawInput state
            wheelChange <- FlightControls.apply_wheel_input rawInput state
            mouseChange <- FlightCamera.apply_mouse_input rawInput state

        PlatformInput.acknowledge_raw_input_wake inputWake
        rawInputReady <- InputAccumulator.work_pending_since observedRevision rawInput

        if FlyState.is_running state then
            let mutable movementChanged =
                match wheelChange with
                | PositionChanged
                | PositionAndDirectionChanged -> true
                | DirectionChanged
                | NoCameraChange ->
                    match mouseChange with
                    | PositionChanged
                    | PositionAndDirectionChanged -> true
                    | DirectionChanged
                    | NoCameraChange -> false

            let mutable directionChanged =
                match wheelChange with
                | DirectionChanged
                | PositionAndDirectionChanged -> true
                | PositionChanged
                | NoCameraChange ->
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

            let now = frameSeconds
            let currentlyMoving = FlightInput.movement_active movement
            let pivotDirection = FlightInput.key_pivot_direction movement

            if pivotDirection <> NoKeyPivot && pivotDirection <> state.key_pivot_direction then
                state.key_pivot_target <- FlightCamera.navigation_target state state.gumball_pivot_target

            state.key_pivot_direction <- pivotDirection

            if movementActive && currentlyMoving then
                let dt = min (now - previousFrameSeconds) MAXIMUM_FRAME_DELTA_SECONDS
                let previousCamera = state.camera

                let movementStep =
                    Movement.step state.config.movement movement state.key_pivot_target dt previousCamera

                let nextCamera = movementStep.camera

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

                if nextCamera.position <> previousCamera.position then
                    movementChanged <- true

                if nextCamera.yaw <> previousCamera.yaw || nextCamera.pitch <> previousCamera.pitch then
                    directionChanged <- true

                state.camera <- nextCamera

            previousFrameSeconds <- now
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
