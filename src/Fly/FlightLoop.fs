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
            match PlatformInput.wait_for_input () with
            | Ok() -> ()
            | Error error -> failwith error

        RhinoApp.Wait()

        FlightControls.update_keyboard_pivot_input state
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
                let requestedPivotDirection = FlightInput.pivot_direction input

                let movement =
                    match state.pivot_input_state, requestedPivotDirection with
                    | PivotInputArmed, _ -> input
                    | WaitingForNeutralPivotInput, NoPivot ->
                        state.pivot_input_state <- PivotInputArmed
                        input
                    | WaitingForNeutralPivotInput, (PivotLeft | PivotRight) -> FlightInput.without_pivot input

                let now = clock.Elapsed.TotalSeconds
                let currentlyMoving = FlightInput.movement_active movement
                let pivotDirection = FlightInput.pivot_direction movement

                if pivotDirection <> NoPivot && pivotDirection <> state.pivot_direction then
                    state.pivot_target <- FlightCamera.pivot_target state.view state.gumball_pivot_target

                state.pivot_direction <- pivotDirection

                if movementActive && currentlyMoving then
                    let dt = min (now - previousFrame) maximum_frame_delta_seconds

                    state.camera <- Movement.step state.config.movement movement state.pivot_target dt state.camera
                    movementChanged <- true

                    if pivotDirection <> NoPivot then
                        directionChanged <- true

                previousFrame <- now
                movementActive <- currentlyMoving

        match movementChanged, directionChanged with
        | true, true -> FlightCamera.apply state PositionAndDirectionChanged
        | true, false -> FlightCamera.apply state PositionChanged
        | false, true -> FlightCamera.apply state DirectionChanged
        | false, false -> ()
