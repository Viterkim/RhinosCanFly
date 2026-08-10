module RhinosCanFly.FlightSession

open System
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
        PlatformInput.wait_for_input ()
        RhinoApp.Wait()

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

let run (view: RhinoView) (config: FlyConfig) (sessionMode: FlightSessionMode) =
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

                match PlatformInput.suspend_mouse_button_overrides () with
                | Ok() -> overridesSuspended <- true
                | Error error -> failwith $"Could not suspend mouse button overrides: {error}"

                let state = FlightState.create view config
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
                        PlatformInput.clear_mouse_hover view
                        PlatformInput.dismiss_native_tooltips state.root_window

                        if state.config.behavior.hide_gumball && originalGumballEnabled then
                            ModelAidSettings.AutoGumballEnabled <- false
                            gumballChanged <- true

                        match PlatformInput.clip_cursor view with
                        | Ok() -> captured <- true
                        | Error error -> failwith error

                        PlatformInput.focus view
                        state.viewport.CameraUp <- Vector3d.ZAxis

                        let rawInputConfig: RawInputConfig =
                            { exit_on_mouse_left = state.config.mouse.exit_on_left
                              exit_on_mouse_right = state.config.mouse.exit_on_right
                              middle_mouse_while_flying = state.config.mouse.middle_button
                              mouse4_pivot_mode = state.config.mouse.mouse4_pivot_mode
                              mouse5_pivot_mode = state.config.mouse.mouse5_pivot_mode
                              mouse4_also_while_flying = state.config.mouse.mouse4_also_while_flying
                              mouse5_also_while_flying = state.config.mouse.mouse5_also_while_flying }

                        let session =
                            PlatformInput.open_raw_input rawInputConfig sessionMode rawInput inputWake

                        raw <- Some session
                        rawInputClean <- false

                        if
                            sessionMode = FlightSessionMode.WhileRightMouseHeld
                            && not (PlatformInput.right_mouse_button_down ())
                        then
                            state.running <- false

                        PlatformInput.hide_cursor ()
                        cursorHidden <- true
                        FlightCamera.apply_entry_lens state
                        FlightRedraw.redraw state.config.behavior.viewport_redraw_mode view
                        FlightLoop.run rawInput state

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
                    FlightCamera.update_navigation_mode rawInput state

                    match FlightCamera.apply_mouse_input rawInput state with
                    | NoCameraChange -> ()
                    | change -> FlightCamera.apply state change)

                if captured then
                    attempt_cleanup cleanupErrors "cursor clip" (fun () ->
                        match PlatformInput.clear_cursor_clip () with
                        | Ok() -> ()
                        | Error error -> failwith error)

                    attempt_cleanup cleanupErrors "cursor position" (fun () ->
                        match PlatformInput.restore_cursor_position state.original_cursor with
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
                            state.config.behavior.save_speed_to_document
                            state.config.movement.minimum_speed
                            state.config.movement.maximum_speed
                            state.speed
                    with
                    | Ok _ -> ()
                    | Error error -> failwith error)

                attempt_cleanup cleanupErrors "redraw" (fun () ->
                    FlightRedraw.redraw state.config.behavior.viewport_redraw_mode view)

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
