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

let state_name () = string sessionState

let recovery_completed () =
    if sessionState = RestartRequired then
        sessionState <- Ready

let viewport_gesture_active (view: RhinoView) = view.MouseCaptured(false)

let viewport_gesture_timeout = TimeSpan.FromMilliseconds 250.
let viewport_gesture_poll_interval = TimeSpan.FromMilliseconds 10.

let release_viewport_gesture (forceRelease: bool) (view: RhinoView) =
    let clock = Stopwatch.StartNew()
    let mutable forced = false

    while viewport_gesture_active view && clock.Elapsed < viewport_gesture_timeout do
        if forceRelease && not forced then
            forced <- true
            RhinoApp.ReleaseMouseCapture() |> ignore
            PlatformInput.record_view_capture_released ()

        PlatformInput.wait_for_input_for viewport_gesture_poll_interval
        RhinoApp.Wait()

    if viewport_gesture_active view then
        PlatformInput.record_view_capture_timed_out ()
        Error "The active viewport did not release its mouse capture within 250 ms."
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
    | RestartRequired -> Error "Input cleanup did not finish safely. Run RhinosCanFlyInputRecover or restart Rhino."
    | Ready ->
        sessionState <- Flying

        let cleanupErrors = ResizeArray<string>()
        let mutable rawInputClean = true
        let mutable inputSafe = true
        let mutable overrideSuspension: InputSuspensionLease option = None
        let mutable keyboardSuppressed = false

        let flightResult =
            try
                match PlatformInput.suspend_mouse_button_overrides InputSuspensionReason.Flight with
                | Ok lease ->
                    overrideSuspension <- Some lease

                    match lease.cleanup_error with
                    | Some error ->
                        inputSafe <- false
                        failwith $"Could not suspend mouse button overrides safely: {error}"
                    | None -> ()
                | Error error -> failwith $"Could not suspend mouse button overrides: {error}"

                let forceCaptureRelease =
                    overrideSuspension
                    |> Option.exists (fun (lease: InputSuspensionLease) -> lease.released_viewport_input)

                match release_viewport_gesture forceCaptureRelease view with
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
                let mutable lensChanged = false
                let mutable flightEntered = false
                let mutable rawInputFailed = false

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
                                FlightExit.request (SessionFailure(error.ToString())) state

                                if PlatformInput.raw_input_start_requires_restart error then
                                    rawInputClean <- false

                                raise error

                        raw <- Some session
                        rawInputClean <- false

                        let heldEntryReleased =
                            sessionMode.lifetime = FlightLifetime.WhileRightMouseHeld
                            && not (PlatformInput.right_mouse_button_down ())

                        if heldEntryReleased then
                            FlightExit.request RightMouseReleased state
                        else
                            let invalidHost =
                                if not (PlatformInput.flight_host_is_active state.host_identity state.view) then
                                    Some HostInvalid
                                elif PlatformInput.foreground_root_window () <> state.root_window then
                                    Some FocusLost
                                else
                                    None

                            match invalidHost with
                            | Some reason ->
                                state.restore_camera_on_exit <- true
                                FlightExit.request reason state
                                failwith "The active Rhino document, viewport, or window changed before flight began."
                            | None -> ()

                            tooltipsChanged <- true
                            CursorTooltipSettings.TooltipsEnabled <- false
                            PlatformInput.clear_mouse_hover view
                            PlatformInput.dismiss_native_tooltips state.root_window

                            if state.config.behavior.hide_gumball && originalGumballEnabled then
                                gumballChanged <- true
                                ModelAidSettings.AutoGumballEnabled <- false

                            match PlatformInput.acquire_cursor_clip view with
                            | Ok lease -> cursorClip <- Some lease
                            | Error error -> failwith error

                            cameraMutated <- true
                            state.viewport.CameraUp <- Vector3d.ZAxis
                            cursorHidden <- true
                            PlatformInput.hide_cursor ()

                            let adjustment = state.config.behavior.lens_adjustment
                            lensChanged <- Option.isSome adjustment.forced_length_mm || adjustment.delta_mm <> 0.
                            FlightCamera.apply_entry_lens state
                            FlightRedraw.redraw state.config.behavior.viewport_redraw_mode view
                            flightEntered <- true
                            FlightLoop.run inputWake rawInput state

                        Ok()
                    with error ->
                        if flightEntered then
                            state.restore_camera_on_exit <- true

                        FlightExit.request (SessionFailure(error.ToString())) state

                        Error(error_message error)

                match raw with
                | Some session ->
                    let stopRequested =
                        attempt_cleanup cleanupErrors "raw input stop request" (fun () ->
                            match PlatformInput.request_raw_input_stop session with
                            | Ok() -> ()
                            | Error error -> failwith error)

                    if not stopRequested then
                        rawInputFailed <- true
                        rawInputClean <- false
                        state.restore_camera_on_exit <- true
                        FlightExit.request (SessionFailure "Could not request raw-input shutdown.") state

                    let runtimeFailed =
                        try
                            PlatformInput.raw_input_runtime_failed session
                        with error ->
                            cleanupErrors.Add $"raw input status: {error_message error}"
                            true

                    if runtimeFailed then
                        rawInputFailed <- true
                        state.restore_camera_on_exit <- true
                        FlightExit.request (SessionFailure "The raw-input worker failed during flight.") state
                | None -> ()

                if keyboardSuppressed then
                    let released =
                        attempt_cleanup cleanupErrors "keyboard input" (fun () ->
                            match PlatformInput.release_flight_keyboard () with
                            | Ok() -> ()
                            | Error error -> failwith error)

                    if released then
                        keyboardSuppressed <- false
                    else
                        inputSafe <- false

                match cursorClip with
                | Some lease ->
                    let released =
                        attempt_cleanup cleanupErrors "cursor clip" (fun () ->
                            match PlatformInput.release_cursor_clip lease with
                            | Ok() -> cursorClip <- None
                            | Error error -> failwith error)

                    if not released then
                        inputSafe <- false
                | None -> ()

                if cursorHidden then
                    let restored =
                        attempt_cleanup cleanupErrors "cursor visibility" (fun () -> PlatformInput.show_cursor ())

                    if not restored then
                        inputSafe <- false

                match raw with
                | None -> ()
                | Some session ->
                    try
                        try
                            let outcome = PlatformInput.close_raw_input session
                            rawInputClean <- outcome.terminated && outcome.registration_relinquished

                            if not (List.isEmpty outcome.errors) then
                                rawInputFailed <- true

                            for error in outcome.errors do
                                cleanupErrors.Add $"raw input shutdown: {error}"
                        with error ->
                            rawInputFailed <- true
                            rawInputClean <- false
                            cleanupErrors.Add $"raw input shutdown: {error_message error}"
                            FlightExit.request (SessionFailure(error.ToString())) state
                    finally
                        raw <- None

                attempt_cleanup cleanupErrors "raw input wake" (fun () ->
                    PlatformInput.dispose_raw_input_wake inputWake)
                |> ignore

                let recordedExitReason =
                    FlyState.exit_reason state
                    |> Option.defaultValue (
                        if state.restore_camera_on_exit then
                            ExplicitRestoreCamera
                        else
                            ExplicitKeepCamera
                    )

                let exitReason =
                    match activeResult with
                    | Error _ when not (FlightExitReason.is_explicit recordedExitReason) -> recordedExitReason
                    | Error error -> SessionFailure error
                    | Ok() when rawInputFailed -> SessionFailure "The raw-input worker failed during flight."
                    | Ok() -> recordedExitReason

                if exitReason <> recordedExitReason then
                    PlatformInput.record_flight_exit
                        (FlightExit.reason_code exitReason)
                        state.root_window
                        (PlatformInput.foreground_root_window ())

                let skipBackgroundDisplay = FlightExitReason.skips_background_display exitReason

                if skipBackgroundDisplay then
                    InputAccumulator.discard_transient_input rawInput
                else
                    attempt_cleanup cleanupErrors "cursor position" (fun () ->
                        match
                            PlatformInput.restore_cursor_position_if_foreground state.root_window state.original_cursor
                        with
                        | Ok() -> ()
                        | Error error -> failwith error)
                    |> ignore

                let restoreCamera =
                    state.restore_camera_on_exit || (cameraMutated && not flightEntered)

                let hostExists = PlatformInput.flight_host_exists state.host_identity state.view

                let cameraRestored =
                    if restoreCamera && hostExists then
                        attempt_cleanup cleanupErrors "camera" (fun () ->
                            CameraSnapshot.restore state.viewport state.original_camera)
                    elif restoreCamera then
                        false
                    else
                        true

                if lensChanged && hostExists then
                    attempt_cleanup cleanupErrors "lens" (fun () ->
                        state.viewport.Camera35mmLensLength <- state.original_lens_length)
                    |> ignore

                let targetRequested =
                    flightEntered
                    && FlightExitReason.is_explicit exitReason
                    && state.config.behavior.auto_pivot_target_on_exit <> AutoPivotTargetMode.Off
                    && (not restoreCamera
                        || (cameraRestored && state.config.behavior.retarget_restored_flights))

                let display_is_safe () =
                    rawInputClean
                    && inputSafe
                    && not skipBackgroundDisplay
                    && PlatformInput.flight_host_is_foreground state.host_identity state.view

                if targetRequested then
                    if display_is_safe () then
                        let started = PlatformInput.record_exit_target_begin ()

                        try
                            attempt_cleanup cleanupErrors "pivot target" (fun () ->
                                ExitPivotTarget.apply state.config.behavior.auto_pivot_target_on_exit state.viewport)
                            |> ignore
                        finally
                            PlatformInput.record_exit_target_end started
                    else
                        PlatformInput.record_exit_target_skipped ()

                if tooltipsChanged then
                    attempt_cleanup cleanupErrors "tooltips" (fun () ->
                        CursorTooltipSettings.TooltipsEnabled <- originalTooltipsEnabled)
                    |> ignore

                if gumballChanged then
                    attempt_cleanup cleanupErrors "gumball" (fun () ->
                        ModelAidSettings.AutoGumballEnabled <- originalGumballEnabled)
                    |> ignore

                if flightEntered && hostExists then
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

                if flightEntered && display_is_safe () then
                    attempt_cleanup cleanupErrors "redraw" (fun () ->
                        FlightRedraw.redraw state.config.behavior.viewport_redraw_mode view)
                    |> ignore

                activeResult
            with error ->
                Error(error_message error)

        if keyboardSuppressed then
            let released =
                attempt_cleanup cleanupErrors "keyboard input" (fun () ->
                    match PlatformInput.release_flight_keyboard () with
                    | Ok() -> ()
                    | Error error -> failwith error)

            if not released then
                inputSafe <- false

        match overrideSuspension with
        | Some lease ->
            let resumed =
                attempt_cleanup cleanupErrors "mouse button overrides" (fun () ->
                    match PlatformInput.resume_mouse_button_overrides lease with
                    | Ok() -> ()
                    | Error error -> failwith error)

            if not resumed then
                inputSafe <- false
        | None -> ()

        if not rawInputClean then
            cleanupErrors.Add "raw input did not shut down cleanly; restart Rhino before using fly mode again"

        if PlatformInput.cursor_clip_recovery_count () > 0 then
            inputSafe <- false

        if not inputSafe then
            cleanupErrors.Add "input cleanup did not finish safely; run RhinosCanFlyInputRecover or restart Rhino"

        sessionState <-
            if rawInputClean && inputSafe then
                Ready
            else
                RestartRequired

        let cleanupMessage = String.concat "; " cleanupErrors

        match flightResult, cleanupErrors.Count with
        | Ok(), 0 -> Ok()
        | Error error, 0 -> Error error
        | Ok(), _ -> Error $"Cleanup failed: {cleanupMessage}"
        | Error error, _ -> Error $"{error}; cleanup failed: {cleanupMessage}"
