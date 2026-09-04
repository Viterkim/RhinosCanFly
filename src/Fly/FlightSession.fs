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
      input_available: Action }

type ActiveSession =
    { state: FlyState
      raw_input: InputAccumulator.State
      input_wake: PlatformInputWake.State
      input_available: Action
      override_suspension: InputSuspensionLease
      cleanup_errors: ResizeArray<string>
      original_gumball_enabled: bool
      mutable raw: PlatformRawInput.Session option
      mutable cursor_clip: CursorClipLease option
      mutable cursor_hidden: bool
      mutable gumball_changed: bool
      mutable perspective_lens_changed: bool
      mutable flight_entered: bool
      mutable keyboard_suppressed: bool
      mutable raw_input_clean: bool
      mutable raw_input_failed: bool
      mutable input_safe: bool }

type SessionState =
    | Ready
    | Starting of StartingSession
    | Flying of ActiveSession
    | Finishing
    | RestartRequired

let mutable session_state = Ready
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

let is_running () =
    match session_state with
    | Starting _
    | Flying _
    | Finishing -> true
    | Ready
    | RestartRequired -> false

let recovery_completed () =
    if session_state = RestartRequired then
        session_state <- Ready

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

    let skip_background_display = FlightExitReason.skips_background_display exit_reason

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

    let restore_camera = state.restore_camera_on_exit

    let host_exists = PlatformInput.viewport_host_exists state.host_identity state.view

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
        session.raw_input_clean
        && session.input_safe
        && not skip_background_display
        && PlatformInput.viewport_host_is_foreground state.host_identity state.view

    if retarget_requested && display_is_safe () then
        attempt_cleanup cleanup_errors "retarget" (fun () ->
            ViewTarget.apply state.config.behavior.retarget retarget_mode state.speed state.view state.viewport)
        |> ignore

    if session.gumball_changed then
        attempt_cleanup cleanup_errors "gumball" (fun () ->
            ModelAidSettings.AutoGumballEnabled <- session.original_gumball_enabled)
        |> ignore

    if session.flight_entered && host_exists then
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

    if session.flight_entered && display_is_safe () then
        attempt_cleanup cleanup_errors "redraw" (fun () -> state.view.Redraw())
        |> ignore

    let override_resumed =
        attempt_cleanup cleanup_errors "mouse button overrides" (fun () ->
            match PlatformMouseActions.resume session.override_suspension with
            | Ok() -> ()
            | Error error -> failwith error)

    if not override_resumed then
        session.input_safe <- false

    if not session.raw_input_clean then
        cleanup_errors.Add "raw input did not shut down cleanly; restart Rhino before using fly mode again"

    if PlatformCursorClip.recovery_count () > 0 then
        session.input_safe <- false

    if not session.input_safe then
        cleanup_errors.Add "input cleanup did not finish safely; run RhinosCanFlyInputRecover or restart Rhino"

    if session.raw_input_clean && session.input_safe then
        PlatformInput.request_application_redraw ()

    session_state <-
        if session.raw_input_clean && session.input_safe then
            Ready
        else
            RestartRequired

    finish_result active_result cleanup_errors

let finish_active (session: ActiveSession) (active_result: Result<unit, string>) =
    try
        finish_active_core session active_result
    finally
        CameraSnapshot.dispose session.state.original_camera

let cleanup_starting (starting: StartingSession) (result: Result<unit, string>) =
    session_state <- Finishing
    let errors = ResizeArray<string>()

    attempt_cleanup errors "keyboard suppression" (fun () -> PlatformFlightKeyboard.stop ())
    |> ignore

    attempt_cleanup errors "main-loop wake" (fun () -> PlatformInputWake.dispose starting.input_wake)
    |> ignore

    let resumed =
        attempt_cleanup errors "mouse button overrides" (fun () ->
            match PlatformMouseActions.resume starting.override_suspension with
            | Ok() -> ()
            | Error error -> failwith error)

    session_state <-
        if resumed && errors.Count = 0 then
            Ready
        else
            RestartRequired

    finish_result result errors

let enter_active (session_mode: FlightSessionMode) (session: ActiveSession) =
    let state = session.state

    PlatformInput.focus_view state.view

    let raw =
        try
            PlatformRawInput.start session.raw_input session.input_available
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

    let navigation_bindings = state.config.bindings.mouse_navigation
    state.keyboard_pivot_held <- FlightControls.is_optional_down navigation_bindings.pivot.hold
    state.keyboard_pan_held <- FlightControls.is_optional_down navigation_bindings.pan.hold

    state.mouse_pivot_hold_buttons <-
        FlightControls.current_mouse_hold_buttons RoutedMouseAction.holds_pivot state.config.mouse

    state.mouse_pan_hold_buttons <-
        FlightControls.current_mouse_hold_buttons RoutedMouseAction.holds_pan state.config.mouse

    let held_entry_released =
        session_mode.lifetime = FlightLifetime.WhileRightMouseHeld
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

        PlatformInput.clear_mouse_hover state.view
        PlatformInput.dismiss_native_tooltips state.host_identity.root_window

        if state.config.behavior.hide_gumball && session.original_gumball_enabled then
            session.gumball_changed <- true
            ModelAidSettings.AutoGumballEnabled <- false

        session.perspective_lens_changed <- FlightCamera.entry_perspective_lens_changes state
        FlightCamera.apply_entry_perspective_lens state

        if ValueOption.isSome state.walking_plane then
            FlightCamera.apply
                state
                { camera_changed = true
                  parallel_magnification = 1. }
        elif session.gumball_changed || session.perspective_lens_changed then
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
              raw = None
              cursor_clip = None
              cursor_hidden = false
              gumball_changed = false
              perspective_lens_changed = false
              flight_entered = false
              keyboard_suppressed = true
              raw_input_clean = true
              raw_input_failed = false
              input_safe = true }

        active_session <- Some session
        created_state <- None

        if
            starting.session_mode.lifetime = FlightLifetime.WhileRightMouseHeld
            && not (PlatformInput.right_mouse_button_down ())
        then
            FlyState.request_exit RightMouseReleased state
        else
            enter_active starting.session_mode session

        if FlyState.is_running state then
            session_state <- Flying session

            let active_result =
                try
                    FlightLoop.run session.input_wake session.raw_input state
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
            if session.flight_entered then
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

            let resumed =
                attempt_cleanup errors "mouse button overrides" (fun () ->
                    match PlatformMouseActions.resume starting.override_suspension with
                    | Ok() -> ()
                    | Error resume_error -> failwith resume_error)

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

    match InputAccumulator.exit_reason starting.raw_input with
    | Some(SessionFailure error) -> cleanup_starting starting (Error error) |> finish_and_report
    | Some _ -> cleanup_starting starting (Ok()) |> finish_and_report
    | None when not (PlatformInput.viewport_host_is_active starting.host_identity starting.view) ->
        cleanup_starting starting (Error "The active Rhino document or viewport changed before flight began.")
        |> finish_and_report
    | None when PlatformInput.foreground_root_window () <> starting.host_identity.root_window ->
        cleanup_starting starting (Error "The Rhino window lost focus before flight began.")
        |> finish_and_report
    | None when not (starting.view.MouseCaptured false) ->
        remove_main_loop_handler ()
        begin_active starting |> finish_and_report
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

let run (view: RhinoView) (config: FlyConfig) (session_mode: FlightSessionMode) =
    match session_state with
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

                    attempt_cleanup errors "mouse button overrides" (fun () ->
                        match PlatformMouseActions.resume suspension with
                        | Ok() -> ()
                        | Error resume_error -> failwith resume_error)
                    |> ignore

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

                        match PlatformFlightKeyboard.start config raw_input input_available with
                        | Ok() -> ()
                        | Error error -> failwith $"Could not suppress flight keys: {error}"

                        let starting =
                            { view = view
                              host_identity = host_identity
                              config = config
                              session_mode = session_mode
                              override_suspension = suspension
                              input_wake = wake
                              raw_input = raw_input
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

                        attempt_cleanup errors "mouse button overrides" (fun () ->
                            match PlatformMouseActions.resume suspension with
                            | Ok() -> ()
                            | Error resume_error -> failwith resume_error)
                        |> ignore

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
    try
        match session_state with
        | Starting starting -> cleanup_starting starting (Error "Rhino is shutting down.") |> ignore
        | Flying session ->
            session.state.restore_camera_on_exit <- true
            FlyState.request_exit (SessionFailure "Rhino is shutting down.") session.state
            finish_active session (Error "Rhino is shutting down.") |> ignore
        | Ready
        | Finishing
        | RestartRequired -> ()
    with error ->
        report $"RhinosCanFly flight shutdown failed: {error_message error}"

    remove_main_loop_handler ()
