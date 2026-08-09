module RhinosCanFly.Runtime

open System
open System.Diagnostics
open Rhino
open Rhino.ApplicationSettings
open Rhino.Display
open Rhino.Geometry

type SessionState =
    | Ready
    | Flying
    | RestartRequired

let mutable sessionState = Ready

let is_running () = sessionState = Flying

let can_start () = sessionState = Ready

let viewport_gesture_active (view: RhinoView) = view.MouseCaptured(false)

let wait_for_viewport_gesture (view: RhinoView) =
    while viewport_gesture_active view do
        match PlatformInput.wait_for_input () with
        | Ok() -> RhinoApp.Wait()
        | Error error -> failwith error

let error_message (error: exn) =
    match error with
    | :? AggregateException as aggregate ->
        aggregate.Flatten().InnerExceptions
        |> Seq.map (fun (inner: exn) -> inner.Message)
        |> String.concat "; "
    | _ -> error.Message

let attempt_cleanup (errors: ResizeArray<string>) (name: string) (action: unit -> unit) =
    try
        action ()
    with error ->
        errors.Add $"{name}: {error_message error}"

let make_state (view: RhinoView) (config: FlyConfig) =
    let viewport = view.ActiveViewport

    let gumballPivotTarget =
        let mutable gumballPlane = Plane.Unset

        if
            not config.pivot_bindings_ignore_gumball
            && RhinoSettings.rotate_view_around_gumball ()
            && view.Document.GetGumballPlane(&gumballPlane)
        then
            Some gumballPlane.Origin
        else
            None

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
      gumball_pivot_target = gumballPivotTarget
      pivot_target = FlightCamera.pivot_target view gumballPivotTarget
      pivot_direction = NoPivot
      pivot_input_state = WaitingForNeutralPivotInput
      mouse_navigation = MouseLook
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

[<Literal>]
let maximum_frame_delta_seconds = 0.05

let run_loop (rawInput: InputAccumulator.State) (state: FlyState) =
    let clock = Stopwatch.StartNew()
    let mutable previousFrame = clock.Elapsed.TotalSeconds
    let mutable movementActive = false

    while state.running do
        if not movementActive then
            match PlatformInput.wait_for_input () with
            | Ok() -> ()
            | Error error -> failwith error

        RhinoApp.Wait()

        FlightCamera.update_mouse_navigation rawInput state
        let mouseChange = FlightCamera.apply_mouse_input rawInput state

        let mutable movementChanged =
            match mouseChange with
            | Some PositionChanged
            | Some PositionAndDirectionChanged -> true
            | Some DirectionChanged
            | None -> false

        let mutable directionChanged =
            match mouseChange with
            | Some DirectionChanged
            | Some PositionAndDirectionChanged -> true
            | Some PositionChanged
            | None -> false

        if state.running then
            match FlightControls.poll rawInput state with
            | None -> ()
            | Some input ->
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

                    state.camera <- Movement.step state.config movement state.pivot_target dt state.camera
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

let run (view: RhinoView) (config: FlyConfig) =
    match sessionState with
    | Flying -> Error "Fly mode is already running."
    | RestartRequired -> Error "Raw input did not shut down cleanly. Restart Rhino before using fly mode again."
    | Ready ->
        sessionState <- Flying

        let cleanupErrors = ResizeArray<string>()
        let mutable rawInputClean = true
        let mutable overridesSuspended = false

        let flightResult =
            try
                wait_for_viewport_gesture view
                PlatformInput.suspend_mouse_button_overrides ()
                overridesSuspended <- true

                let state = make_state view config
                let rawInput = InputAccumulator.create ()
                let originalTooltipsEnabled = CursorTooltipSettings.TooltipsEnabled
                let originalGumballEnabled = ModelAidSettings.AutoGumballEnabled
                let mutable raw: PlatformInput.RawInputSession option = None
                let mutable captured = false
                let mutable cursorHidden = false
                let mutable tooltipsChanged = false
                let mutable gumballChanged = false
                let inputWake = PlatformInput.create_raw_input_wake ()

                let activeResult =
                    try
                        CursorTooltipSettings.TooltipsEnabled <- false
                        tooltipsChanged <- true
                        PlatformInput.clear_mouse_hover view.Handle
                        PlatformInput.dismiss_native_tooltips state.root_window

                        if state.config.hide_gumball_while_flying && originalGumballEnabled then
                            ModelAidSettings.AutoGumballEnabled <- false
                            gumballChanged <- true

                        let rectangle = view.ScreenRectangle

                        match PlatformInput.clip_cursor rectangle with
                        | Ok() -> captured <- true
                        | Error error -> failwith error

                        PlatformInput.focus view.Handle
                        state.viewport.CameraUp <- Vector3d.ZAxis

                        let session =
                            PlatformInput.open_raw_input
                                state.config
                                rawInput
                                (PlatformInput.raw_input_wake_action inputWake)

                        raw <- Some session
                        rawInputClean <- false
                        PlatformInput.hide_cursor ()
                        cursorHidden <- true
                        FlightCamera.apply_entry_lens state
                        FlightCamera.redraw state.config.viewport_redraw_mode view
                        run_loop rawInput state

                        Ok()
                    with error ->
                        Error(error_message error)

                attempt_cleanup cleanupErrors "raw input shutdown" (fun () ->
                    match raw with
                    | Some session ->
                        PlatformInput.close_raw_input session
                        raw <- None
                        rawInputClean <- true
                    | None -> ())

                attempt_cleanup cleanupErrors "final mouse input" (fun () ->
                    FlightCamera.update_mouse_navigation rawInput state

                    match FlightCamera.apply_mouse_input rawInput state with
                    | Some change -> FlightCamera.apply state change
                    | None -> ())

                if captured then
                    attempt_cleanup cleanupErrors "cursor clip" (fun () ->
                        match PlatformInput.clear_cursor_clip () with
                        | Ok() -> ()
                        | Error error -> failwith error)

                    attempt_cleanup cleanupErrors "cursor position" (fun () ->
                        match PlatformInput.set_cursor_position state.original_cursor with
                        | Ok() -> ()
                        | Error error -> failwith error)

                if cursorHidden then
                    attempt_cleanup cleanupErrors "cursor visibility" (fun () -> PlatformInput.show_cursor ())

                attempt_cleanup cleanupErrors "lens" (fun () ->
                    state.viewport.Camera35mmLensLength <- state.original_lens_length)

                if tooltipsChanged then
                    attempt_cleanup cleanupErrors "tooltips" (fun () ->
                        CursorTooltipSettings.TooltipsEnabled <- originalTooltipsEnabled)

                if gumballChanged then
                    attempt_cleanup cleanupErrors "gumball" (fun () ->
                        ModelAidSettings.AutoGumballEnabled <- originalGumballEnabled)

                attempt_cleanup cleanupErrors "speed" (fun () ->
                    match
                        FlightSpeed.set
                            view.Document
                            state.config.save_speed_to_document
                            state.config.minimum_speed
                            state.config.maximum_speed
                            state.speed
                    with
                    | Ok _ -> ()
                    | Error error -> failwith error)

                attempt_cleanup cleanupErrors "redraw" (fun () ->
                    FlightCamera.redraw state.config.viewport_redraw_mode view)

                activeResult
            with error ->
                Error(error_message error)

        if overridesSuspended then
            attempt_cleanup cleanupErrors "mouse button overrides" (fun () ->
                PlatformInput.resume_mouse_button_overrides ())

        if not rawInputClean then
            cleanupErrors.Add "raw input did not shut down cleanly; restart Rhino before using fly mode again"

        sessionState <- if rawInputClean then Ready else RestartRequired

        let cleanupMessage = cleanupErrors |> Seq.toList |> String.concat "; "

        match flightResult, cleanupErrors.Count with
        | Ok(), 0 -> Ok()
        | Error error, 0 -> Error error
        | Ok(), _ -> Error $"Cleanup failed: {cleanupMessage}"
        | Error error, _ -> Error $"{error}; cleanup failed: {cleanupMessage}"
