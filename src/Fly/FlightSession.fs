module RhinosCanFly.FlightSession

open System
open System.Diagnostics
open Rhino
open Rhino.ApplicationSettings
open Rhino.Display

type StartingSession =
    { view: RhinoView
      host_identity: ViewportHostIdentity
      config: FlyConfig
      session_mode: FlightSessionMode
      override_suspension: InputSuspensionLease
      input_wake: PlatformInputWake.State
      raw_input: InputAccumulator.State
      capture_deadline: int64
      held_entry: (unit -> bool) option
      buttons_swapped: bool
      input_available: Action }

type ActiveSession =
    { state: FlyState
      raw_input: InputAccumulator.State
      input_wake: PlatformInputWake.State
      input_available: Action
      override_suspension: InputSuspensionLease
      cleanup_errors: ResizeArray<string>
      original_gumball_enabled: bool
      crosshair: FlightCrosshair option
      mutable raw: PlatformRawInput.Session option
      mutable cursor_clip: CursorClipLease option
      mutable cursor_hidden: bool
      mutable gumball_changed: bool
      mutable perspective_lens_changed: bool
      mutable flight_entered: bool
      mutable keyboard_suppressed: bool
      mutable raw_input_clean: bool
      mutable raw_input_failed: bool
      mutable input_safe: bool
      mutable finalizing: bool
      mutable final_result: Result<unit, string> option }

type SessionState =
    | Ready
    | Starting of StartingSession
    | Flying of ActiveSession
    | Finishing
    | RestartRequired

let mutable session_state = Ready
let mutable shutting_down = false
let mutable main_loop_handler_installed = false
let mutable processing_main_loop = false
let mutable main_loop_handler: EventHandler = null

let report (message: string) =
    Debug.WriteLine message

    try
        RhinoApp.WriteLine message
    with error ->
        Debug.WriteLine $"RhinosCanFly output failed: {error.Message}"

let error_message (error: exn) =
    match error with
    | :? AggregateException as aggregate ->
        aggregate.Flatten().InnerExceptions
        |> Seq.map (fun (inner: exn) -> inner.Message)
        |> String.concat "; "
    | _ -> error.Message

let remove_main_loop_handler () =
    if main_loop_handler_installed then
        try
            RhinoApp.MainLoop.RemoveHandler main_loop_handler
            main_loop_handler_installed <- false
        with error ->
            report $"RhinosCanFly main-loop cleanup failed: {error_message error}"

let attempt_cleanup (errors: ResizeArray<string>) (name: string) (action: unit -> unit) =
    try
        action ()
        true
    with error ->
        errors.Add $"{name}: {error_message error}"
        false

let resume_mouse_overrides (errors: ResizeArray<string>) (lease: InputSuspensionLease) =
    if shutting_down then
        true
    else
        attempt_cleanup errors "mouse button overrides" (fun () ->
            match PlatformMouseActions.resume lease with
            | Ok() -> ()
            | Error error -> failwith error)

let is_running () =
    match session_state with
    | Starting _
    | Flying _
    | Finishing -> true
    | Ready
    | RestartRequired -> false

let recovery_completed () =
    match session_state with
    | RestartRequired when not shutting_down -> session_state <- Ready
    | _ -> ()

let finish_result (flight_result: Result<unit, string>) (errors: ResizeArray<string>) =
    if errors.Count = 0 then
        flight_result
    else
        let cleanup_message = String.concat "; " errors

        match flight_result with
        | Ok() -> Error $"Cleanup failed: {cleanup_message}"
        | Error error -> Error $"{error}; cleanup failed: {cleanup_message}"

let finish_active_core (session: ActiveSession) (active_result: Result<unit, string>) =
    session_state <- Finishing
    let state = session.state
    let cleanup_errors = session.cleanup_errors

    match session.crosshair with
    | Some crosshair ->
        attempt_cleanup cleanup_errors "crosshair" (fun () -> crosshair.Enabled <- false)
        |> ignore
    | None -> ()

    if session.keyboard_suppressed then
        let released =
            attempt_cleanup cleanup_errors "keyboard suppression" (fun () ->
                PlatformFlightKeyboard.stop ()
                session.keyboard_suppressed <- false)

        if not released then
            session.input_safe <- false

    match session.raw with
    | Some raw ->
        let stop_requested =
            attempt_cleanup cleanup_errors "raw input stop request" (fun () ->
                match PlatformRawInput.request_stop raw with
                | Ok() -> ()
                | Error error -> failwith error)

        if not stop_requested then
            session.raw_input_failed <- true
            session.raw_input_clean <- false
            state.restore_camera_on_exit <- true
            FlyState.request_exit (SessionFailure "Could not request raw-input shutdown.") state

        let runtime_failed =
            try
                PlatformRawInput.runtime_failed raw
            with error ->
                cleanup_errors.Add $"raw input status: {error_message error}"
                true

        if runtime_failed then
            session.raw_input_failed <- true
            state.restore_camera_on_exit <- true
            FlyState.request_exit (SessionFailure "The raw-input worker failed during flight.") state
    | None -> ()

    match session.cursor_clip with
    | Some lease ->
        let released =
            attempt_cleanup cleanup_errors "cursor clip" (fun () ->
                match PlatformCursorClip.release lease with
                | Ok() -> session.cursor_clip <- None
                | Error error -> failwith error)

        if not released then
            session.input_safe <- false
    | None -> ()

    match session.raw with
    | None -> ()
    | Some raw ->
        try
            try
                let outcome = PlatformRawInput.stop raw

                session.raw_input_clean <-
                    outcome.terminated
                    && outcome.registration_relinquished
                    && not outcome.previous_registration_lost

                if not (List.isEmpty outcome.errors) then
                    session.raw_input_failed <- true

                for error in outcome.errors do
                    cleanup_errors.Add $"raw input shutdown: {error}"
            with error ->
                session.raw_input_failed <- true
                session.raw_input_clean <- false
                cleanup_errors.Add $"raw input shutdown: {error_message error}"
                FlyState.request_exit (SessionFailure(error.ToString())) state
        finally
            session.raw <- None

    attempt_cleanup cleanup_errors "raw input wake" (fun () -> PlatformInputWake.dispose session.input_wake)
    |> ignore

    let recorded_exit_reason =
        state.exit_reason
        |> Option.defaultValue (
            if state.restore_camera_on_exit then
                ExplicitRestoreCamera
            else
                ExplicitKeepCamera
        )

    let exit_reason =
        match active_result with
        | Error _ when not (FlightExitReason.is_explicit recorded_exit_reason) -> recorded_exit_reason
        | Error error -> SessionFailure error
        | Ok() when session.raw_input_failed -> SessionFailure "The raw-input worker failed during flight."
        | Ok() -> recorded_exit_reason

    let skip_background_display =
        shutting_down || FlightExitReason.skips_background_display exit_reason

    if skip_background_display then
        InputAccumulator.discard_transient_input session.raw_input
    else
        attempt_cleanup cleanup_errors "cursor position" (fun () ->
            match
                PlatformInput.restore_cursor_position_if_foreground
                    state.host_identity.root_window
                    state.original_cursor
            with
            | Ok() -> ()
            | Error error -> failwith error)
        |> ignore

    if session.cursor_hidden then
        let restored =
            attempt_cleanup cleanup_errors "cursor visibility" (fun () ->
                PlatformInput.show_cursor ()
                session.cursor_hidden <- false)

        if not restored then
            session.input_safe <- false

    let active_result =
        match active_result, exit_reason with
        | Ok(), SessionFailure error -> Error error
        | result, _ -> result

    let restore_camera =
        state.restore_camera_on_exit
        || match exit_reason with
           | SessionFailure _ -> true
           | _ -> false

    let host_exists =
        try
            PlatformInput.viewport_host_exists state.host_identity state.view
        with error ->
            cleanup_errors.Add $"viewport lookup: {error_message error}"
            false

    let camera_restored =
        if restore_camera && host_exists then
            attempt_cleanup cleanup_errors "camera" (fun () ->
                CameraSnapshot.restore state.viewport state.original_camera)
        elif restore_camera then
            false
        else
            true

    if
        session.perspective_lens_changed
        && host_exists
        && state.projection <> ViewProjectionKind.Parallel
    then
        attempt_cleanup cleanup_errors "perspective lens" (fun () ->
            match state.original_camera.perspective_lens_length with
            | ValueSome(PerspectiveLensLengthMm lens) -> state.viewport.Camera35mmLensLength <- lens
            | ValueNone -> failwith "The original perspective lens length is unavailable.")
        |> ignore

    let retarget_mode =
        if restore_camera then
            state.config.behavior.retarget.on_restored_flight_exit
        else
            state.config.behavior.retarget.on_flight_exit

    let retarget_requested =
        session.flight_entered
        && FlightExitReason.is_explicit exit_reason
        && retarget_mode <> RetargetMode.Off
        && (not restore_camera || camera_restored)

    let display_is_safe () =
        try
            session.raw_input_clean
            && session.input_safe
            && not shutting_down
            && not skip_background_display
            && PlatformInput.viewport_host_is_foreground state.host_identity state.view
        with error ->
            cleanup_errors.Add $"foreground lookup: {error_message error}"
            false

    if retarget_requested && display_is_safe () then
        attempt_cleanup cleanup_errors "retarget" (fun () ->
            ViewTarget.apply state.config.behavior.retarget retarget_mode state.view state.viewport)
        |> ignore

    if session.gumball_changed then
        attempt_cleanup cleanup_errors "gumball" (fun () ->
            ModelAidSettings.AutoGumballEnabled <- session.original_gumball_enabled)
        |> ignore

    state.hidden_gumball_plane <- ValueNone

    if not shutting_down && session.flight_entered && host_exists then
        attempt_cleanup cleanup_errors "speed" (fun () ->
            match
                FlightSpeed.set
                    state.view.Document
                    state.config.behavior.save_speed_to_document
                    state.config.movement.speed_range
                    state.speed
            with
            | Ok _ -> ()
            | Error error -> failwith error)
        |> ignore

    if
        (session.flight_entered || Option.isSome session.crosshair)
        && display_is_safe ()
    then
        attempt_cleanup cleanup_errors "redraw" (fun () -> state.view.Redraw())
        |> ignore

    let override_resumed =
        resume_mouse_overrides cleanup_errors session.override_suspension

    if not override_resumed then
        session.input_safe <- false

    if not session.raw_input_clean then
        cleanup_errors.Add "raw input did not shut down cleanly; restart Rhino before using fly mode again"

    if PlatformCursorClip.recovery_count () > 0 then
        session.input_safe <- false

    if not session.input_safe then
        cleanup_errors.Add "input cleanup did not finish safely; run RhinosCanFlyInputRecover or restart Rhino"

    if not shutting_down && session.raw_input_clean && session.input_safe then
        attempt_cleanup cleanup_errors "application redraw" PlatformInput.request_application_redraw
        |> ignore

    finish_result active_result cleanup_errors

let finish_active (session: ActiveSession) (active_result: Result<unit, string>) =
    match session.final_result with
    | Some result -> result
    | None when session.finalizing -> Error "Flight cleanup is already in progress."
    | None ->
        session.finalizing <- true
        let mutable core_completed = false

        let result =
            try
                let result = finish_active_core session active_result
                core_completed <- true
                result
            with error ->
                session_state <- RestartRequired
                finish_result (Error(error_message error)) session.cleanup_errors

        let disposal_errors = ResizeArray<string>()

        attempt_cleanup disposal_errors "camera snapshot" (fun () ->
            CameraSnapshot.dispose session.state.original_camera)
        |> ignore

        let completed = finish_result result disposal_errors

        session_state <-
            if
                core_completed
                && disposal_errors.Count = 0
                && session.raw_input_clean
                && session.input_safe
            then
                Ready
            else
                RestartRequired

        session.final_result <- Some completed
        completed

let cleanup_starting (starting: StartingSession) (result: Result<unit, string>) =
    session_state <- Finishing
    let errors = ResizeArray<string>()

    attempt_cleanup errors "keyboard suppression" (fun () -> PlatformFlightKeyboard.stop ())
    |> ignore

    attempt_cleanup errors "main-loop wake" (fun () -> PlatformInputWake.dispose starting.input_wake)
    |> ignore

    let resumed = resume_mouse_overrides errors starting.override_suspension

    session_state <-
        if resumed && errors.Count = 0 then
            Ready
        else
            RestartRequired

    finish_result result errors

let enter_active (starting: StartingSession) (session: ActiveSession) =
    let state = session.state

    PlatformInput.focus_view state.view

    if not (FlyState.is_running state) then
        failwith "Flight was cancelled during viewport activation."

    if
        starting.held_entry
        |> Option.exists (fun (valid: unit -> bool) -> not (valid ()))
    then
        failwith "The held flight entry was released or replaced before startup."

    match PlatformMouseActions.raw_mouse_admission starting.buttons_swapped with
    | ValueNone -> failwith "Rhino owns an unfinished mouse interaction. Release the buttons before starting flight."
    | ValueSome buttons -> PlatformFlightKeyboard.admit_mouse_bindings buttons

    let raw =
        try
            PlatformRawInput.start starting.buttons_swapped session.raw_input session.input_available
        with error ->
            FlyState.request_exit (SessionFailure(error.ToString())) state

            let restart_required =
                match error with
                | :? PlatformRawInput.StartFailureException as failure -> failure.RestartRequired
                | _ -> false

            if restart_required then
                session.raw_input_clean <- false

            raise error

    session.raw <- Some raw
    session.raw_input_clean <- false

    if not (FlyState.is_running state) then
        failwith "Flight was cancelled during raw-input startup."

    let navigation_bindings = state.config.bindings.mouse_navigation
    state.keyboard_pivot_held <- FlightControls.is_optional_down navigation_bindings.pivot.hold
    state.keyboard_pan_held <- FlightControls.is_optional_down navigation_bindings.pan.hold

    state.mouse_pivot_hold_buttons <-
        FlightControls.current_mouse_hold_buttons RoutedMouseAction.holds_pivot state.config.mouse

    state.mouse_pan_hold_buttons <-
        FlightControls.current_mouse_hold_buttons RoutedMouseAction.holds_pan state.config.mouse

    let held_entry_released =
        starting.session_mode.lifetime = FlightLifetime.WhileRightMouseHeld
        && not (PlatformInput.right_mouse_button_down ())

    if held_entry_released then
        FlyState.request_exit RightMouseReleased state
    else
        if not (PlatformInput.viewport_host_is_active state.host_identity state.view) then
            state.restore_camera_on_exit <- true
            FlyState.request_exit HostInvalid state
            failwith "The Rhino viewport changed before flight began."

        if PlatformInput.foreground_root_window () <> state.host_identity.root_window then
            state.restore_camera_on_exit <- true
            FlyState.request_exit FocusLost state
            failwith "The Rhino window lost focus before flight began."

        match PlatformCursorClip.acquire state.view with
        | Ok lease -> session.cursor_clip <- Some lease
        | Error error -> failwith error

        session.cursor_hidden <- true
        PlatformInput.hide_cursor ()

        PlatformInput.prepare_viewport_for_navigation state.view state.host_identity.root_window

        if not (FlyState.is_running state) then
            failwith "Flight was cancelled during viewport preparation."

        if state.config.behavior.hide_gumball && session.original_gumball_enabled then
            state.hidden_gumball_plane <- ViewTarget.gumball_plane state.view
            session.gumball_changed <- true
            ModelAidSettings.AutoGumballEnabled <- false

        match session.crosshair with
        | Some crosshair -> crosshair.Enabled <- true
        | None -> ()

        session.perspective_lens_changed <- FlightCamera.entry_perspective_lens_changes state
        FlightCamera.apply_entry_perspective_lens state

        if ValueOption.isSome state.walking_plane then
            if
                FlightCamera.write_view
                    state
                    { camera_changed = true
                      parallel_magnification = 1. }
            then
                FlightCamera.redraw state
        elif
            session.gumball_changed
            || session.perspective_lens_changed
            || Option.isSome session.crosshair
        then
            state.view.Redraw()

        session.flight_entered <- true

let begin_active (starting: StartingSession) =
    let mutable active_session: ActiveSession option = None
    let mutable created_state: FlyState option = None

    try
        let original_gumball_enabled = ModelAidSettings.AutoGumballEnabled

        let state =
            FlightState.create starting.view starting.host_identity starting.config starting.session_mode

        created_state <- Some state

        let session =
            { state = state
              raw_input = starting.raw_input
              input_wake = starting.input_wake
              input_available = starting.input_available
              override_suspension = starting.override_suspension
              cleanup_errors = ResizeArray<string>()
              original_gumball_enabled = original_gumball_enabled
              crosshair =
                if state.config.behavior.crosshair.enabled then
                    Some(FlightCrosshair state)
                else
                    None
              raw = None
              cursor_clip = None
              cursor_hidden = false
              gumball_changed = false
              perspective_lens_changed = false
              flight_entered = false
              keyboard_suppressed = true
              raw_input_clean = true
              raw_input_failed = false
              input_safe = true
              finalizing = false
              final_result = None }

        active_session <- Some session
        created_state <- None
        // Entry can reenter Rhino. Own the resources before it does.
        session_state <- Flying session

        let owns_session () =
            not session.finalizing
            && (match session_state with
                | Flying current -> obj.ReferenceEquals(current, session)
                | _ -> false)

        state.camera_write_allowed <-
            fun () ->
                not shutting_down
                && owns_session ()
                && Option.isNone (InputAccumulator.exit_reason session.raw_input)
                && not (System.Threading.Volatile.Read(&session.raw_input.escape_requested))
                && PlatformInput.viewport_id_matches state.host_identity state.view
                && PlatformInput.foreground_root_window () = state.host_identity.root_window

        if
            starting.session_mode.lifetime = FlightLifetime.WhileRightMouseHeld
            && not (PlatformInput.right_mouse_button_down ())
        then
            FlyState.request_exit RightMouseReleased state
        else
            enter_active starting session

        if FlyState.is_running state && owns_session () then
            let active_result =
                try
                    match session.raw with
                    | Some raw -> FlightLoop.run session.input_wake session.raw_input raw state
                    | None -> failwith "Flight has no raw-input session."

                    Ok()
                with error ->
                    state.restore_camera_on_exit <- true
                    FlyState.request_exit (SessionFailure(error.ToString())) state
                    Error(error_message error)

            finish_active session active_result
        else
            finish_active session (Ok())
    with error ->
        let message = error_message error

        match active_session with
        | Some session ->
            session.state.restore_camera_on_exit <- true

            FlyState.request_exit (SessionFailure(error.ToString())) session.state
            finish_active session (Error message)
        | None ->
            let errors = ResizeArray<string>()

            attempt_cleanup errors "keyboard suppression" (fun () -> PlatformFlightKeyboard.stop ())
            |> ignore

            match created_state with
            | Some state ->
                attempt_cleanup errors "camera snapshot" (fun () -> CameraSnapshot.dispose state.original_camera)
                |> ignore
            | None -> ()

            attempt_cleanup errors "main-loop wake" (fun () -> PlatformInputWake.dispose starting.input_wake)
            |> ignore

            let resumed = resume_mouse_overrides errors starting.override_suspension

            session_state <-
                if resumed && errors.Count = 0 then
                    Ready
                else
                    RestartRequired

            finish_result (Error message) errors

let finish_and_report (result: Result<unit, string>) =
    match result with
    | Ok() -> ()
    | Error error -> report $"RhinosCanFly failed: {error}"

let process_starting (starting: StartingSession) =
    PlatformInputWake.acknowledge starting.input_wake

    PlatformFlightKeyboard.consume_escape_exit
        starting.session_mode.lifetime
        starting.config.mouse.exit_on_left
        starting.config.mouse.exit_on_right
        starting.raw_input

    match InputAccumulator.exit_reason starting.raw_input with
    | Some(SessionFailure error) -> cleanup_starting starting (Error error) |> finish_and_report
    | Some _ -> cleanup_starting starting (Ok()) |> finish_and_report
    | None when
        starting.held_entry
        |> Option.exists (fun (valid: unit -> bool) -> not (valid ()))
        ->
        cleanup_starting starting (Ok()) |> finish_and_report
    | None when not (PlatformInput.viewport_host_is_active starting.host_identity starting.view) ->
        cleanup_starting starting (Error "The active Rhino document or viewport changed before flight began.")
        |> finish_and_report
    | None when PlatformInput.foreground_root_window () <> starting.host_identity.root_window ->
        cleanup_starting starting (Error "The Rhino window lost focus before flight began.")
        |> finish_and_report
    | None when not (starting.view.MouseCaptured false) ->
        remove_main_loop_handler ()
        begin_active starting |> finish_and_report
    | None when Stopwatch.GetTimestamp() >= starting.capture_deadline ->
        cleanup_starting starting (Error "Rhino kept mouse capture for two seconds. Flight entry was cancelled.")
        |> finish_and_report
    | None -> PlatformInputWake.signal starting.input_wake

let process_main_loop () =
    if not processing_main_loop then
        processing_main_loop <- true

        try
            try
                match session_state with
                | Starting starting -> process_starting starting
                | Flying _ -> ()
                | Ready
                | Finishing
                | RestartRequired -> ()
            with error ->
                match session_state with
                | Starting starting -> cleanup_starting starting (Error(error_message error)) |> finish_and_report
                | Flying session ->
                    session.state.restore_camera_on_exit <- true
                    FlyState.request_exit (SessionFailure(error.ToString())) session.state
                    finish_active session (Error(error_message error)) |> finish_and_report
                | Finishing ->
                    session_state <- RestartRequired
                    report $"RhinosCanFly main-loop cleanup failed: {error_message error}"
                | Ready
                | RestartRequired -> report $"RhinosCanFly main-loop handler failed: {error_message error}"
        finally
            processing_main_loop <- false

            match session_state with
            | Starting _ -> ()
            | Ready
            | Flying _
            | Finishing
            | RestartRequired -> remove_main_loop_handler ()

do main_loop_handler <- EventHandler(fun (_: obj) (_: EventArgs) -> process_main_loop ())

let ensure_main_loop_handler () =
    if not main_loop_handler_installed then
        RhinoApp.MainLoop.AddHandler main_loop_handler
        main_loop_handler_installed <- true

let run (view: RhinoView) (config: FlyConfig) (session_mode: FlightSessionMode) (held_entry: (unit -> bool) option) =
    match session_state with
    | _ when shutting_down -> Error "Rhino is shutting down."
    | Starting _
    | Flying _
    | Finishing -> Error "Fly mode is already running."
    | RestartRequired -> Error "Input cleanup did not finish safely. Run RhinosCanFlyInputRecover or restart Rhino."
    | Ready ->
        try
            match PlatformMouseActions.suspend () with
            | Error error -> Error $"Could not suspend mouse button overrides: {error}"
            | Ok suspension ->
                match suspension.cleanup_error with
                | Some error ->
                    let errors = ResizeArray<string>()

                    resume_mouse_overrides errors suspension |> ignore

                    session_state <- RestartRequired
                    finish_result (Error $"Could not suspend mouse button overrides safely: {error}") errors
                | None ->
                    let mutable pending_wake: PlatformInputWake.State option = None

                    try
                        let host_identity = PlatformInput.capture_viewport_host view
                        let wake = PlatformInputWake.create host_identity.root_window
                        pending_wake <- Some wake
                        let raw_input = InputAccumulator.create ()
                        let input_available = Action(fun () -> PlatformInputWake.signal wake)

                        let buttons_swapped =
                            match PlatformFlightKeyboard.start config raw_input input_available with
                            | Ok swapped -> swapped
                            | Error error -> failwith $"Could not suppress flight keys: {error}"

                        let starting =
                            { view = view
                              host_identity = host_identity
                              config = config
                              session_mode = session_mode
                              override_suspension = suspension
                              input_wake = wake
                              raw_input = raw_input
                              capture_deadline = Stopwatch.GetTimestamp() + 2L * Stopwatch.Frequency
                              held_entry = held_entry
                              buttons_swapped = buttons_swapped
                              input_available = input_available }

                        session_state <- Starting starting
                        pending_wake <- None

                        try
                            if view.MouseCaptured false then
                                ensure_main_loop_handler ()
                                PlatformInputWake.signal wake
                                Ok()
                            else
                                begin_active starting
                        with error ->
                            cleanup_starting starting (Error(error_message error))
                    with error ->
                        let errors = ResizeArray<string>()

                        attempt_cleanup errors "keyboard suppression" (fun () -> PlatformFlightKeyboard.stop ())
                        |> ignore

                        match pending_wake with
                        | Some wake ->
                            attempt_cleanup errors "main-loop wake" (fun () -> PlatformInputWake.dispose wake)
                            |> ignore
                        | None -> ()

                        resume_mouse_overrides errors suspension |> ignore

                        session_state <- if errors.Count = 0 then Ready else RestartRequired
                        finish_result (Error(error_message error)) errors
        with error ->
            match session_state with
            | Starting starting -> cleanup_starting starting (Error(error_message error))
            | Flying session -> finish_active session (Error(error_message error))
            | Ready
            | Finishing
            | RestartRequired -> Error(error_message error)

let shutdown () =
    shutting_down <- true

    try
        match session_state with
        | Starting starting -> cleanup_starting starting (Ok()) |> finish_and_report
        | Flying session ->
            session.state.restore_camera_on_exit <- true
            FlyState.request_exit HostInvalid session.state
            // Let the running session unwind and clean up.
            InputAccumulator.request_exit HostInvalid session.raw_input
        | Ready
        | Finishing
        | RestartRequired -> ()
    with error ->
        report $"RhinosCanFly flight shutdown failed: {error_message error}"

    remove_main_loop_handler ()
