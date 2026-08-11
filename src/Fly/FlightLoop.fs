module RhinosCanFly.FlightLoop

open System.Diagnostics
open Rhino

[<Literal>]
let maximum_frame_delta_seconds = 0.05

let run (rawInput: InputAccumulator.State) (state: FlyState) =
    let clock = Stopwatch.StartNew()
    let mutable previousFrame = clock.Elapsed.TotalSeconds
    let mutable movementActive = false

    while state.running do
        if not movementActive then
            PlatformInput.wait_for_input ()

        RhinoApp.Wait()

        FlightControls.update_keyboard_navigation_input state
        FlightCamera.update_navigation_mode rawInput state
        let mouseChange = FlightCamera.apply_mouse_input rawInput state

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

        if state.running then
            FlightControls.update_state rawInput state

            if state.running then
                let input = FlightControls.read_movement state
                let requestedPivotDirection = FlightInput.key_pivot_direction input

                let movement =
                    match state.key_pivot_input_state, requestedPivotDirection with
                    | KeyPivotInputArmed, _ -> input
                    | WaitingForNeutralKeyPivotInput, NoKeyPivot ->
                        state.key_pivot_input_state <- KeyPivotInputArmed
                        input
                    | WaitingForNeutralKeyPivotInput, (KeyPivotLeft | KeyPivotRight) ->
                        FlightInput.without_key_pivot input

                let now = clock.Elapsed.TotalSeconds
                let currentlyMoving = FlightInput.movement_active movement
                let pivotDirection = FlightInput.key_pivot_direction movement

                if pivotDirection <> NoKeyPivot && pivotDirection <> state.key_pivot_direction then
                    state.key_pivot_target <- FlightCamera.navigation_target state.view state.gumball_pivot_target

                state.key_pivot_direction <- pivotDirection

                if movementActive && currentlyMoving then
                    let dt = min (now - previousFrame) maximum_frame_delta_seconds

                    state.camera <- Movement.step state.config.movement movement state.key_pivot_target dt state.camera
                    movementChanged <- true

                    if pivotDirection <> NoKeyPivot then
                        directionChanged <- true

                previousFrame <- now
                movementActive <- currentlyMoving

        // keep this boring without 'match movementChanged, directionChanged'
        // because it fucking spits out a 'newobj System.Tuple<bool, bool>' on every iter (so heap gg)
        if movementChanged then
            if directionChanged then
                FlightCamera.apply state PositionAndDirectionChanged
            else
                FlightCamera.apply state PositionChanged
        elif directionChanged then
            FlightCamera.apply state DirectionChanged
