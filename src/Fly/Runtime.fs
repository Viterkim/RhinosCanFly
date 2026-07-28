module RhinosCanFly.Runtime

open System.Diagnostics
open Rhino
open Rhino.ApplicationSettings
open Rhino.Display
open Rhino.Geometry

let mutable sessionRunning = false

let is_running () = sessionRunning

let viewport_gesture_active (view: RhinoView) = view.MouseCaptured(false)

let wait_for_viewport_gesture (view: RhinoView) =
    while viewport_gesture_active view do
        match PlatformInput.wait_for_input () with
        | Ok() -> RhinoApp.Wait()
        | Error error -> failwith error

let make_state (view: RhinoView) (config: FlyConfig) =
    let viewport = view.ActiveViewport

    let original_cursor =
        match PlatformInput.get_cursor_position () with
        | Ok point -> point
        | Error error -> failwith error

    let yaw, pitch = Movement.angles_from_direction viewport.CameraDirection

    let root_window = PlatformInput.root_window view.Handle

    { view = view
      viewport = viewport
      config = config
      root_window = root_window
      original_cursor = original_cursor
      original_lens_length = viewport.Camera35mmLensLength
      running = true
      camera =
        { position = viewport.CameraLocation
          yaw = yaw
          pitch = pitch }
      speed =
        FlightSpeed.current
            view.Document
            config.load_speed_from_document
            config.minimum_speed
            config.maximum_speed
            config.base_speed
      boost_enabled = false
      boost_was_down = FlightControls.is_down config.boost_toggle
      slow_enabled = false
      slow_was_down = FlightControls.is_down config.slow
      speed_increase_was_down = FlightControls.is_optional_down config.speed_increase
      speed_decrease_was_down = FlightControls.is_optional_down config.speed_decrease }

let run (view: RhinoView) (config: FlyConfig) =
    if sessionRunning then
        Error "Fly mode is already running."
    else
        sessionRunning <- true

        try
            wait_for_viewport_gesture view
            PlatformInput.suspend_mouse_button_overrides ()

            try
                let state = make_state view config
                let rawInput = InputAccumulator.create ()
                let originalTooltipsEnabled = CursorTooltipSettings.TooltipsEnabled
                let mutable raw: PlatformInput.RawInputSession option = None
                let mutable captured = false
                let mutable cursorHidden = false
                let mutable tooltipsChanged = false
                let inputWake = PlatformInput.create_raw_input_wake ()

                try
                    CursorTooltipSettings.TooltipsEnabled <- false
                    tooltipsChanged <- true

                    let rectangle = view.ScreenRectangle

                    match PlatformInput.clip_cursor rectangle with
                    | Ok() -> captured <- true
                    | Error error -> failwith error

                    PlatformInput.focus view.Handle
                    state.viewport.CameraUp <- Vector3d.ZAxis

                    raw <-
                        Some(
                            PlatformInput.open_raw_input
                                state.config
                                rawInput
                                (PlatformInput.raw_input_wake_action inputWake)
                        )

                    let mouseExitState = FlightControls.create_mouse_exit_state ()
                    PlatformInput.hide_cursor ()
                    cursorHidden <- true
                    PlatformInput.clear_mouse_hover view.Handle
                    FlightCamera.apply_entry_lens state
                    FlightCamera.redraw view
                    let clock = Stopwatch.StartNew()
                    let mutable previousFrame = clock.Elapsed.TotalSeconds
                    let mutable movementActive = false

                    while state.running do
                        if not movementActive then
                            match PlatformInput.wait_for_input () with
                            | Ok() -> ()
                            | Error error -> failwith error

                        RhinoApp.Wait()
                        PlatformInput.reset_raw_input_wake inputWake

                        let mouseChanged = FlightCamera.apply_mouse_look rawInput state
                        let mutable movementChanged = false

                        if state.running then
                            match FlightControls.poll rawInput state mouseExitState with
                            | None -> ()
                            | Some movement ->
                                let now = clock.Elapsed.TotalSeconds
                                let currentlyMoving = FlightControls.movement_active movement

                                if movementActive && currentlyMoving then
                                    let dt = min (now - previousFrame) 0.05

                                    state.camera <- Movement.step state.config movement dt state.camera
                                    movementChanged <- true

                                previousFrame <- now
                                movementActive <- currentlyMoving

                        if mouseChanged || movementChanged then
                            FlightCamera.apply state mouseChanged movementChanged

                    Ok()
                finally
                    try
                        match raw with
                        | Some session ->
                            PlatformInput.close_raw_input session
                            raw <- None
                        | None -> ()

                        let mouseChanged = FlightCamera.apply_mouse_look rawInput state

                        if mouseChanged then
                            FlightCamera.apply state true false

                        if captured then
                            PlatformInput.clear_cursor_clip () |> ignore
                            PlatformInput.set_cursor_position state.original_cursor |> ignore

                        if cursorHidden then
                            PlatformInput.show_cursor ()

                        state.viewport.Camera35mmLensLength <- state.original_lens_length

                        match
                            FlightSpeed.set
                                view.Document
                                state.config.save_speed_to_document
                                state.config.minimum_speed
                                state.config.maximum_speed
                                state.speed
                        with
                        | Ok _ -> ()
                        | Error error -> RhinoApp.WriteLine $"RhinosCanFly: {error}"
                    finally
                        try
                            if tooltipsChanged then
                                CursorTooltipSettings.TooltipsEnabled <- originalTooltipsEnabled
                        finally
                            FlightCamera.redraw view
            with error ->
                Error error.Message
        finally
            sessionRunning <- false
            PlatformInput.resume_mouse_button_overrides ()
