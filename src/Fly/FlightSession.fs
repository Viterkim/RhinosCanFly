module RhinosCanFly.FlightSession

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

let viewport_gesture_timeout = TimeSpan.FromSeconds 2.
let viewport_gesture_poll_interval = TimeSpan.FromMilliseconds 20.

let release_viewport_gesture (view: RhinoView) =
    let clock = Stopwatch.StartNew()

    while viewport_gesture_active view && clock.Elapsed < viewport_gesture_timeout do
        PlatformInput.wait_for_input_for viewport_gesture_poll_interval
        RhinoApp.Wait()

    if viewport_gesture_active view then
        PlatformInput.record_view_capture_timed_out ()
        Error "The active viewport did not release its mouse capture within two seconds."
    else
        Ok()

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
        true
    with error ->
        errors.Add $"{name}: {error_message error}"
        false

let run (view: RhinoView) (config: FlyConfig) (sessionMode: FlightSessionMode) =
    match sessionState with
    | Flying -> Error "Fly mode is already running."
    | RestartRequired -> Error "Raw input did not shut down cleanly. Restart Rhino before using fly mode again."
    | Ready ->
        sessionState <- Flying

        let cleanupErrors = ResizeArray<string>()
        let mutable rawInputClean = true
        let mutable overridesSuspended = false
        let mutable keyboardSuppressed = false

        let flightResult =
            try
                match PlatformInput.suspend_mouse_button_overrides () with
                | Ok() -> overridesSuspended <- true
                | Error error -> failwith $"Could not suspend mouse button overrides: {error}"

                match release_viewport_gesture view with
                | Ok() -> ()
                | Error error -> failwith error

                match PlatformInput.suppress_flight_keyboard config.bindings with
                | Ok() -> keyboardSuppressed <- true
                | Error error -> failwith $"Could not suppress flight keyboard input: {error}"

                let state = FlightState.create view config sessionMode
                let rawInput = InputAccumulator.create ()
                let originalTooltipsEnabled = CursorTooltipSettings.TooltipsEnabled
                let originalGumballEnabled = ModelAidSettings.AutoGumballEnabled
                let inputWake = PlatformInput.create_raw_input_wake ()
                let inputAvailable = PlatformInput.raw_input_wake_action inputWake
                let mutable raw: PlatformInput.RawInputSession option = None
                let mutable cursorClip: CursorClipLease option = None
                let mutable cursorHidden = false
                let mutable tooltipsChanged = false
                let mutable gumballChanged = false
                let mutable cameraMutated = false
                let mutable flightEntered = false

                let activeResult =
                    try
                        let rawInputConfig: RawInputConfig =
                            { exit_on_mouse_left = state.config.mouse.exit_on_left
                              exit_on_mouse_right = state.config.mouse.exit_on_right
                              middle_mouse_while_flying = state.config.mouse.middle_button
                              mouse4_pivot_mode = state.config.mouse.mouse4_pivot_mode
                              mouse5_pivot_mode = state.config.mouse.mouse5_pivot_mode
                              mouse4_also_while_flying = state.config.mouse.mouse4_also_while_flying
                              mouse5_also_while_flying = state.config.mouse.mouse5_also_while_flying }

                        let session =
                            try
                                PlatformInput.open_raw_input rawInputConfig sessionMode rawInput inputAvailable
                            with error ->
                                if PlatformInput.raw_input_start_requires_restart error then
                                    rawInputClean <- false

                                raise error

                        raw <- Some session
                        rawInputClean <- false

                        if
                            sessionMode.lifetime = FlightLifetime.WhileRightMouseHeld
                            && not (PlatformInput.right_mouse_button_down ())
                        then
                            state.running <- false

                        CursorTooltipSettings.TooltipsEnabled <- false
                        tooltipsChanged <- true
                        PlatformInput.clear_mouse_hover view
                        PlatformInput.dismiss_native_tooltips state.root_window

                        if state.config.behavior.hide_gumball && originalGumballEnabled then
                            ModelAidSettings.AutoGumballEnabled <- false
                            gumballChanged <- true

                        match PlatformInput.acquire_cursor_clip view with
                        | Ok lease -> cursorClip <- Some lease
                        | Error error -> failwith error

                        state.viewport.CameraUp <- Vector3d.ZAxis
                        cameraMutated <- true
                        PlatformInput.hide_cursor ()
                        cursorHidden <- true
                        FlightCamera.apply_entry_lens state
                        FlightRedraw.redraw state.config.behavior.viewport_redraw_mode view
                        flightEntered <- true
                        FlightLoop.run inputWake rawInput state
                        Ok()
                    with error ->
                        if flightEntered then
                            state.restore_camera_on_exit <- true

                        Error(error_message error)

                match raw with
                | Some session ->
                    PlatformInput.request_raw_input_stop session

                    if PlatformInput.raw_input_runtime_failed session then
                        state.restore_camera_on_exit <- true
                | None -> ()

                match cursorClip with
                | Some lease ->
                    attempt_cleanup cleanupErrors "cursor clip" (fun () ->
                        match PlatformInput.release_cursor_clip lease with
                        | Ok() -> cursorClip <- None
                        | Error error -> failwith error)
                    |> ignore
                | None -> ()

                if cursorHidden then
                    attempt_cleanup cleanupErrors "cursor visibility" (fun () -> PlatformInput.show_cursor ())
                    |> ignore

                attempt_cleanup cleanupErrors "cursor position" (fun () ->
                    match
                        PlatformInput.restore_cursor_position_if_foreground state.root_window state.original_cursor
                    with
                    | Ok() -> ()
                    | Error error -> failwith error)
                |> ignore

                let restoreCamera =
                    state.restore_camera_on_exit || (cameraMutated && not flightEntered)

                let cameraRestored =
                    if restoreCamera then
                        attempt_cleanup cleanupErrors "camera" (fun () ->
                            CameraSnapshot.restore state.viewport state.original_camera)
                    else
                        true

                attempt_cleanup cleanupErrors "lens" (fun () ->
                    state.viewport.Camera35mmLensLength <- state.original_lens_length)
                |> ignore

                if
                    flightEntered
                    && state.config.behavior.auto_pivot_target_on_exit <> AutoPivotTargetMode.Off
                    && (not restoreCamera
                        || (cameraRestored && state.config.behavior.retarget_restored_flights))
                then
                    attempt_cleanup cleanupErrors "pivot target" (fun () ->
                        ExitPivotTarget.apply state.config.behavior.auto_pivot_target_on_exit state.viewport)
                    |> ignore

                if tooltipsChanged then
                    attempt_cleanup cleanupErrors "tooltips" (fun () ->
                        CursorTooltipSettings.TooltipsEnabled <- originalTooltipsEnabled)
                    |> ignore

                if gumballChanged then
                    attempt_cleanup cleanupErrors "gumball" (fun () ->
                        ModelAidSettings.AutoGumballEnabled <- originalGumballEnabled)
                    |> ignore

                if flightEntered then
                    attempt_cleanup cleanupErrors "speed" (fun () ->
                        match
                            FlightSpeed.set
                                view.Document
                                state.config.behavior.save_speed_to_document
                                state.config.movement.speed_range
                                state.speed
                        with
                        | Ok _ -> ()
                        | Error error -> failwith error)
                    |> ignore

                attempt_cleanup cleanupErrors "redraw" (fun () ->
                    FlightRedraw.redraw state.config.behavior.viewport_redraw_mode view)
                |> ignore

                if keyboardSuppressed then
                    let released =
                        attempt_cleanup cleanupErrors "keyboard input" (fun () ->
                            match PlatformInput.release_flight_keyboard () with
                            | Ok() -> ()
                            | Error error -> failwith error)

                    if released then
                        keyboardSuppressed <- false

                let wakeCanBeDisposed =
                    match raw with
                    | None -> rawInputClean
                    | Some session ->
                        let outcome = PlatformInput.close_raw_input session
                        raw <- None
                        rawInputClean <- outcome.terminated && outcome.registration_relinquished

                        for error in outcome.errors do
                            cleanupErrors.Add $"raw input shutdown: {error}"

                        outcome.terminated

                if wakeCanBeDisposed then
                    attempt_cleanup cleanupErrors "raw input wake" (fun () ->
                        PlatformInput.dispose_raw_input_wake inputWake)
                    |> ignore

                activeResult
            with error ->
                Error(error_message error)

        if keyboardSuppressed then
            attempt_cleanup cleanupErrors "keyboard input" (fun () ->
                match PlatformInput.release_flight_keyboard () with
                | Ok() -> ()
                | Error error -> failwith error)
            |> ignore

        if overridesSuspended then
            attempt_cleanup cleanupErrors "mouse button overrides" (fun () ->
                match PlatformInput.resume_mouse_button_overrides () with
                | Ok() -> ()
                | Error error -> failwith error)
            |> ignore

        if not rawInputClean then
            cleanupErrors.Add "raw input did not shut down cleanly; restart Rhino before using fly mode again"

        sessionState <- if rawInputClean then Ready else RestartRequired

        let cleanupMessage = String.concat "; " cleanupErrors

        match flightResult, cleanupErrors.Count with
        | Ok(), 0 -> Ok()
        | Error error, 0 -> Error error
        | Ok(), _ -> Error $"Cleanup failed: {cleanupMessage}"
        | Error error, _ -> Error $"{error}; cleanup failed: {cleanupMessage}"
