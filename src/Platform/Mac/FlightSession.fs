module RhinosCanFly.FlightSession

open System
open System.Diagnostics
open Eto.Forms
open Rhino
open Rhino.ApplicationSettings
open Rhino.Commands
open Rhino.Display
open RhinosCanFly.Platform.Mac

type Entry =
    { navigation: ViewportNavigation.Operation option
      target_point: NavigationTargetPoint
      valid: unit -> bool
      held: (unit -> bool) option
      held_buttons: uint32
      entry_press: MouseEntryPress option
      mouse_entry: bool
      context: MacNative.InputEvent option }

let flight_entry =
    { navigation = None
      target_point = NavigationTargetPoint.ViewCenter
      valid = fun () -> true
      held = None
      held_buttons = 0u
      entry_press = None
      mouse_entry = false
      context = None }

type Session =
    { state: FlyState
      input: InputAccumulator.State
      original_gumball: bool
      entry: Entry
      mutable transport: MacNavigationInput.Session option
      mutable wake: MacInputWake.Connection option
      mutable loop: FlightLoop.State option
      mutable crosshair: FlightCrosshair option
      mutable gumball_changed: bool
      mutable lens_changed: bool
      mutable inspected_at: int64
      mutable entered: bool
      mutable ending: bool
      mutable finalizing: bool }

type PendingStart =
    { view: RhinoView
      navigation: ViewportNavigation.Operation option
      start: unit -> unit }

let mutable current: Session option = None
let mutable timer: UITimer option = None
let mutable processing = false
let mutable shutting_down = false
let mutable pending_start: PendingStart option = None

let is_running () = Option.isSome current

let request_exit (reason: FlightExitReason) (session: Session) =
    match session.transport, MacNavigationInput.current with
    | Some transport, Some active when obj.ReferenceEquals(transport, active) -> MacNavigationInput.request_stop reason
    | _ -> ()

    let accepted =
        if
            session.transport
            |> Option.exists (fun (transport: MacNavigationInput.Session) -> transport.worker)
        then
            InputAccumulator.exit_reason session.input |> Option.defaultValue reason
        else
            PlatformFlightKeyboard.resolve_pending_exit
                session.state.session_mode.lifetime
                session.state.config.mouse.exit_buttons
                session.input
            |> Option.orElseWith (fun () -> InputAccumulator.exit_reason session.input)
            |> Option.defaultValue reason

    FlyState.request_exit accepted session.state

let direct_loop (session: Session) (operation: ViewportNavigation.Operation) : FlightLoop.State =
    let state = session.state
    let timeline = InputAccumulator.timeline_buffer_for session.input
    let mutable viewport_dirty = false
    let mutable discard_remaining_pointer = false
    let mutable pending_change = ViewChange.none
    let mutable changed = false

    let mouse: ViewportNavigation.MouseConfig =
        { x_mode = state.config.mouse.x_mode
          y_mode = state.config.mouse.y_mode
          sensitivity = state.config.movement.parallel_projection.mouse_sensitivity
          pivot_multiplier = state.config.movement.parallel_projection.mouse_pivot_multiplier
          pan_multiplier = state.config.movement.parallel_projection.mouse_pan_multiplier }

    let can_write () =
        if FlyState.can_write_camera state then
            if state.viewport.IsParallelProjection then
                true
            else
                request_exit ExplicitKeepCamera session
                false
        else
            false

    let sync_camera () =
        if viewport_dirty && can_write () then
            FlightCamera.sync_camera_from_viewport ValueNone state
            viewport_dirty <- false

    let publish () =
        if can_write () then
            let published = FlightCamera.write_view state pending_change
            changed <- changed || published
            viewport_dirty <- viewport_dirty || (published && pending_change.parallel_magnification <> 1.)
            sync_camera ()

        pending_change <- ViewChange.none

    let apply_effect (effect: InputEffect) =
        if can_write () then
            let changed = FlightCamera.write_view state effect.view_change

            if effect.pointer_rebase_required then
                FlightCamera.rebase_active_pivot state
                state.wheel_remainder <- 0L
                InputAccumulator.discard_pointer_input session.input
                PlatformFlightKeyboard.discard_pointer_input ()
                discard_remaining_pointer <- true

            changed
        else
            false

    let step () =
        discard_remaining_pointer <- false

        let struct (count, overflow) =
            InputAccumulator.drain_timeline timeline session.input

        if overflow then
            request_exit (SessionFailure "The input timeline overflowed.") session

        can_write () |> ignore
        changed <- false

        for index = 0 to count - 1 do
            let event = timeline[index]

            if can_write () then
                match event.kind with
                | InputAccumulator.TimelineEventKind.Movement when not discard_remaining_pointer ->
                    match operation with
                    | ViewportNavigation.Operation.ParallelPan ->
                        if pending_change.parallel_magnification <> 1. then
                            publish ()

                        let previous = state.camera
                        let units = FlightCamera.pan_units_per_radian previous.target previous

                        state.camera <-
                            Movement.mouse_pan
                                state.config.mouse
                                mouse.sensitivity
                                mouse.pan_multiplier
                                units
                                event.dx
                                event.dy
                                previous

                        pending_change <- ViewChange.combine pending_change (ViewChange.camera previous state.camera)
                    | _ ->
                        let struct (steps, factor) =
                            ViewportNavigation.parallel_zoom_steps ViewSettings.ZoomScale event.dy

                        let magnification = Math.Pow(factor, float steps)
                        FlightCamera.record_parallel_magnification magnification state

                        pending_change <-
                            ViewChange.combine
                                pending_change
                                { camera_changed = false
                                  parallel_magnification = magnification }
                | InputAccumulator.TimelineEventKind.Wheel when not discard_remaining_pointer ->
                    let factor =
                        ViewportNavigation.wheel_magnification (PlatformInput.wheel_zoom_steps event.wheel)

                    FlightCamera.record_parallel_magnification factor state

                    pending_change <-
                        ViewChange.combine
                            pending_change
                            { camera_changed = false
                              parallel_magnification = factor }
                | InputAccumulator.TimelineEventKind.KeyboardTransition ->
                    publish ()

                    let actions =
                        PlatformFlightKeyboard.apply_keyboard_transition event.key event.key_down

                    let effect =
                        if event.terminal then
                            InputEffect.none
                        else
                            FlightControls.apply_keyboard_actions actions state

                    if not event.terminal then
                        changed <- apply_effect effect || changed
                | InputAccumulator.TimelineEventKind.RawMouseButton ->
                    publish ()

                    let effect =
                        FlightControls.apply_raw_mouse_button_transition_with_terminal event.terminal event.button state

                    if not event.terminal then
                        changed <- apply_effect effect || changed
                | InputAccumulator.TimelineEventKind.ExitKeepCamera
                | InputAccumulator.TimelineEventKind.ExitRestoreCamera
                | InputAccumulator.TimelineEventKind.ExitHeldRelease
                | InputAccumulator.TimelineEventKind.ExitCancelledEntry ->
                    FlightLoop.apply_ordered_exit publish event.kind state
                | _ -> ()

        publish ()

        InputAccumulator.exit_reason session.input
        |> Option.iter (fun (reason: FlightExitReason) -> request_exit reason session)

        if changed && can_write () then
            sync_camera ()
            FlightCamera.redraw state

    { step = step
      work_pending = fun () -> InputAccumulator.work_pending session.input
      wait_timeout = fun () -> 100 }

let finish (session: Session) =
    if not session.finalizing then
        request_exit (session.state.exit_reason |> Option.defaultValue ExplicitKeepCamera) session
        session.finalizing <- true
        let state = session.state
        let errors = ResizeArray<string>()

        let cleanup (name: string) (action: unit -> unit) =
            try
                action ()
            with error ->
                errors.Add $"{name}: {error.Message}"

        let reason = state.exit_reason |> Option.defaultValue ExplicitKeepCamera

        if not (FlightExitReason.is_explicit reason) then
            pending_start <- None

        session.crosshair
        |> Option.iter (fun (crosshair: FlightCrosshair) -> cleanup "crosshair" (fun () -> crosshair.Enabled <- false))

        session.transport
        |> Option.iter (fun (transport: MacNavigationInput.Session) ->
            cleanup "input" (fun () -> MacNavigationInput.stop transport reason |> Option.iter failwith))

        session.wake
        |> Option.iter (fun (wake: MacInputWake.Connection) ->
            cleanup "input wake" (fun () -> (wake :> IDisposable).Dispose()))

        session.wake <- None

        PlatformFlightKeyboard.stop ()

        let host_exists () =
            PlatformInput.viewport_host_exists state.host_identity state.view

        let restore =
            state.restore_camera_on_exit
            || (match reason with
                | SessionFailure _ -> true
                | _ -> false)

        if restore && host_exists () then
            cleanup "camera" (fun () -> CameraSnapshot.restore state.viewport state.original_camera host_exists)

        if
            session.lens_changed
            && host_exists ()
            && not state.viewport.IsParallelProjection
        then
            cleanup "lens" (fun () ->
                match state.original_camera.perspective_lens_length with
                | ValueSome(PerspectiveLensLengthMm lens) -> state.viewport.Camera35mmLensLength <- lens
                | ValueNone -> ())

        let foreground () =
            not shutting_down
            && PlatformInput.viewport_host_is_foreground state.host_identity state.view

        let mode =
            if restore then
                state.config.behavior.retarget.on_restored_flight_exit
            else
                state.config.behavior.retarget.on_flight_exit

        if
            session.entered
            && errors.Count = 0
            && (FlightExitReason.is_explicit reason || reason = EntryCancelled)
            && foreground ()
        then
            if reason <> EntryCancelled && Option.isNone session.entry.navigation then
                cleanup "retarget" (fun () ->
                    ViewTarget.apply state.config.behavior.retarget mode state.view state.viewport foreground)

            cleanup "cursor position" (fun () ->
                let (CursorPosition point) = state.original_cursor

                let result =
                    MacNative.CGWarpMouseCursorPosition(MacNative.Point(float point.X, float point.Y))

                if result <> 0 then
                    failwith $"CoreGraphics cursor restoration failed ({result})."

                let result = MacNative.CGAssociateMouseAndMouseCursorPosition 1u

                if result <> 0 then
                    MacNavigationInput.detached <- true
                    failwith $"CoreGraphics cursor association failed ({result}).")

        if session.gumball_changed then
            cleanup "gumball" (fun () -> ModelAidSettings.AutoGumballEnabled <- session.original_gumball)

        state.hidden_gumball_plane <- ValueNone

        if
            session.entered
            && reason <> EntryCancelled
            && Option.isNone session.entry.navigation
            && host_exists ()
        then
            cleanup "speed" (fun () ->
                match
                    FlightSpeed.set
                        state.view.Document
                        state.config.behavior.save_speed_to_document
                        state.config.movement.speed_range
                        state.speed
                with
                | Ok _ -> ()
                | Error error -> failwith error)

        if session.entered && foreground () then
            cleanup "redraw" (fun () -> state.view.Redraw())

        cleanup "camera snapshot" (fun () -> CameraSnapshot.dispose state.original_camera)
        current <- None

        match reason with
        | SessionFailure error -> errors.Insert(0, error)
        | _ -> ()

        if errors.Count <> 0 then
            let message = String.concat "; " errors

            try
                RhinoApp.WriteLine $"RhinosCanFly: {message}"
            with error ->
                Debug.WriteLine error

let activate (session: Session) (transport: MacNavigationInput.Session) =
    let state = session.state
    let view = state.view
    let host = state.host_identity
    let config = state.config
    let mode = state.session_mode
    let entry = session.entry
    let wake = session.wake |> Option.get

    state.camera_write_allowed <-
        fun () ->
            not session.finalizing
            && MacNavigationInput.is_current transport
            && Option.isNone (InputAccumulator.exit_reason session.input)

    let ensure_entry () =
        if
            not (entry.valid ())
            || (entry.held |> Option.exists (fun (held: unit -> bool) -> not (held ())))
        then
            raise (OperationCanceledException "The navigation entry was cancelled.")

        if not (FlyState.validate_camera_host state) then
            invalidOp "The navigation entry was cancelled."

    ensure_entry ()

    let navigation = config.bindings.mouse_navigation
    state.keyboard_pivot_held <- FlightControls.is_optional_down navigation.pivot.hold
    state.keyboard_pan_held <- FlightControls.is_optional_down navigation.pan.hold

    entry.navigation
    |> Option.iter (fun (operation: ViewportNavigation.Operation) ->
        let navigation_mode =
            if operation = ViewportNavigation.Operation.Pivot then
                ViewNavigationMode.Pivot
            else
                ViewNavigationMode.Pan

        state.latched_mouse_navigation <-
            if operation = ViewportNavigation.Operation.Pivot then
                PivotNavigation
            else
                PanNavigation

        if
            operation = ViewportNavigation.Operation.Pivot
            || operation = ViewportNavigation.Operation.Pan
        then
            let target =
                ViewTarget.apply_for_navigation
                    config.behavior
                    navigation_mode
                    view
                    state.viewport
                    entry.target_point
                    (fun () -> FlyState.validate_camera_host state)
                    (fun (_original: Rhino.Geometry.Point3d) (target: Rhino.Geometry.Point3d) ->
                        FlightCamera.sync_camera_from_viewport (ValueSome target) state)

            state.active_mouse_navigation <-
                if operation = ViewportNavigation.Operation.Pivot then
                    MousePivot(FlightCamera.create_pivot_drag target state)
                else
                    MousePan(target, FlightCamera.pan_units_per_radian target state.camera))

    ensure_entry ()

    if config.behavior.hide_gumball && session.original_gumball then
        state.hidden_gumball_plane <- ViewTarget.gumball_plane view
        session.gumball_changed <- true
        ModelAidSettings.AutoGumballEnabled <- false

    ensure_entry ()

    if Option.isNone entry.navigation && config.behavior.crosshair.enabled then
        let crosshair = FlightCrosshair state
        session.crosshair <- Some crosshair
        crosshair.Enabled <- true

    ensure_entry ()

    if Option.isNone entry.navigation then
        session.lens_changed <- FlightCamera.entry_perspective_lens_changes state
        FlightCamera.apply_entry_perspective_lens state

    ensure_entry ()

    if ValueOption.isSome state.walking_plane then
        FlightCamera.write_view
            state
            { camera_changed = true
              parallel_magnification = 1. }
        |> ignore

    ensure_entry ()

    session.loop <-
        match entry.navigation with
        | Some ViewportNavigation.Operation.ParallelPan ->
            Some(direct_loop session ViewportNavigation.Operation.ParallelPan)
        | Some ViewportNavigation.Operation.ParallelZoom ->
            Some(direct_loop session ViewportNavigation.Operation.ParallelZoom)
        | _ ->
            Some(
                FlightLoop.create
                    (fun () ->
                        // Intentional transport stop leaves its exit reason for the shared controls.
                        Ok(
                            Option.isSome (InputAccumulator.exit_reason session.input)
                            || MacNavigationInput.is_current transport
                        ))
                    ignore
                    session.input
                    state
            )

    MacNavigationInput.activate transport entry.mouse_entry entry.context
    session.entered <- true
    view.Redraw()
    wake.Request()


let stop (reason: FlightExitReason) =
    pending_start <- None

    match current with
    | Some session when
        session.entered
        && not shutting_down
        && FlightExitReason.is_explicit reason
        && Option.isNone (InputAccumulator.exit_reason session.input)
        && FlyState.is_running session.state
        && (session.transport
            |> Option.exists (fun (transport: MacNavigationInput.Session) -> transport.worker && transport.active))
        ->
        if not session.ending then
            let code = if reason = ExplicitRestoreCamera then 1u else 0u

            if (MacNative.load ()).raw_finish.Invoke code = 0 then
                session.ending <- true
            else
                request_exit reason session
                finish session
    | Some session ->
        request_exit reason session
        finish session
    | None -> MacNavigationInput.request_stop reason

let finish_pending_exit () =
    match current with
    | Some session when not session.finalizing ->
        match session.state.exit_reason, InputAccumulator.exit_reason session.input with
        | Some _, _ -> finish session
        | None, Some reason -> stop reason
        | None, None -> ()
    | _ -> ()

let capture_failure (error: uint32) =
    match error with
    | 8u -> FocusLost
    | 4u -> SessionFailure "Mac input stopped: the event tap was disabled."
    | 5u -> SessionFailure "Mac input stopped: the event tap was disabled by user input."
    | 11u -> SessionFailure "Mac input stopped: the native input queue overflowed."
    | 12u -> SessionFailure "Mac input stopped: the capture configuration is invalid."
    | 13u -> SessionFailure "Mac input stopped: a repaired key press could not be paired reliably."
    | 14u -> SessionFailure "Mac input stopped: Rhino still owns the scrolling gesture."
    | _ -> SessionFailure "The Mac input capture session is no longer available."

let pulse () =
    if not processing then
        processing <- true

        let active_wake =
            match current with
            | Some session -> session.wake
            | None -> None

        let mutable began = false
        let mutable continue_work = false
        let mutable wait_for_key = false

        try
            match current with
            | Some session when
                not session.finalizing
                && (match active_wake with
                    | Some wake -> wake.TryBegin()
                    | None -> false)
                ->
                began <- true

                try
                    match InputAccumulator.exit_reason session.input with
                    | Some reason -> request_exit reason session
                    | None -> ()

                    let native = MacNative.load ()

                    if not session.entered && FlyState.is_running session.state then
                        let startup =
                            session.transport
                            |> Option.map MacNavigationInput.complete_start
                            |> Option.defaultValue 0u

                        match startup with
                        | 1u when
                            not (session.entry.valid ())
                            || (session.entry.held |> Option.exists (fun (held: unit -> bool) -> not (held ())))
                            ->
                            request_exit EntryCancelled session
                        | 1u -> activate session (Option.get session.transport)
                        | 3u -> ()
                        | _ ->
                            let detail =
                                match native.raw_error.Invoke() with
                                | 1u -> "event tap enumeration failed"
                                | 2u -> "event tap acquisition failed (check Accessibility permission)"
                                | 3u -> "the event tap lacks the required keyboard or mouse events"
                                | _ -> "the worker run loop could not start"

                            request_exit (SessionFailure $"Mac pointer startup failed: {detail}.") session

                    let native_work = native.raw_drain.Invoke()

                    if native_work = 3u then
                        request_exit (capture_failure (native.raw_error.Invoke())) session

                    let work_due =
                        match session.loop with
                        | Some loop -> loop.work_pending () || loop.wait_timeout () = 0
                        | None -> false

                    let now = Stopwatch.GetTimestamp()

                    if session.entered && native.raw_available.Invoke() = 0u then
                        request_exit (capture_failure (native.raw_error.Invoke())) session

                    if session.entered && now - session.inspected_at >= Stopwatch.Frequency / 120L then
                        session.inspected_at <- now

                        match session.entry.navigation with
                        | Some ViewportNavigation.Operation.ParallelPan
                        | Some ViewportNavigation.Operation.ParallelZoom ->
                            PlatformFlightKeyboard.reconcile_physical_keys ()
                        | _ -> ()

                        if native.raw_validate.Invoke() = 0u then
                            request_exit (capture_failure (native.raw_error.Invoke())) session

                    if
                        not (PlatformInput.viewport_host_is_foreground session.state.host_identity session.state.view)
                    then
                        request_exit FocusLost session

                    match session.entry.navigation with
                    | Some ViewportNavigation.Operation.ParallelPan
                    | Some ViewportNavigation.Operation.ParallelZoom when
                        not session.state.viewport.IsParallelProjection
                        ->
                        request_exit ExplicitKeepCamera session
                    | _ -> ()

                    match session.loop with
                    | Some loop when work_due || not (FlyState.is_running session.state) -> loop.step ()
                    | _ -> ()

                    match session.entry.navigation with
                    | Some ViewportNavigation.Operation.Pivot
                    | Some ViewportNavigation.Operation.Pan when
                        session.entered && session.state.latched_mouse_navigation = LookNavigation
                        ->
                        request_exit ExplicitKeepCamera session
                    | _ -> ()

                    let native_pending = native.raw_pending.Invoke()

                    let pending_key =
                        not (
                            session.transport
                            |> Option.exists (fun (transport: MacNavigationInput.Session) -> transport.worker)
                        )
                        && (native_pending = 2u
                            || (native_pending <> 1u && native.keyboard_boundary.Invoke() <> 0.))

                    if
                        session.entered
                        && not session.finalizing
                        && FlyState.is_running session.state
                        && (match current with
                            | Some active -> obj.ReferenceEquals(active, session)
                            | None -> false)
                    then
                        wait_for_key <- pending_key

                        continue_work <-
                            not pending_key
                            && (native_pending = 1u
                                || (match session.loop with
                                    | Some loop -> loop.work_pending ()
                                    | None -> false))
                with
                | :? OperationCanceledException -> request_exit EntryCancelled session
                | error -> request_exit (SessionFailure error.Message) session

                if not (FlyState.is_running session.state) then
                    finish session
            | _ -> ()

            MacNavigationInput.complete_cleanup ()
            |> Option.iter (fun (error: string) -> Debug.WriteLine error)

            if Option.isNone current && not (MacNavigationInput.pending_cleanup ()) then
                match pending_start with
                | Some request ->
                    pending_start <- None

                    try
                        request.start ()
                    with error ->
                        Debug.WriteLine error
                | None -> ()

            if Option.isNone current && not (MacNavigationInput.pending_cleanup ()) then
                match timer with
                | Some running ->
                    running.Stop()
                    running.Dispose()
                    timer <- None
                | None -> ()
        finally
            if began then
                match active_wake with
                | Some wake -> wake.Complete(continue_work, wait_for_key)
                | None -> ()

            processing <- false

// MainLoop also covers pumps that skip beforeWaiting.
let main_loop = EventHandler(fun (_: obj) (_: EventArgs) -> pulse ())

let command_began =
    EventHandler<CommandEventArgs>(fun (_: obj) (event: CommandEventArgs) ->
        match event.CommandEnglishName with
        | "RhinosCanFly"
        | "RhinosCanFlyTempFly"
        | "RhinosCanWalk"
        | "RhinosCanFlyMouseEntry"
        | "RhinosCanFlyPivot"
        | "RhinosCanFlyPan"
        | "RhinosCanFlyInputStatus"
        | "RhinosCanFlyInputRecover" -> ()
        | _ ->
            try
                MacNavigationInput.request_stop ExplicitKeepCamera
                stop ExplicitKeepCamera
            with error ->
                Debug.WriteLine error)

let ensure_scheduler () =
    if Option.isNone timer then
        let running = new UITimer(Interval = 1. / 120.)
        running.Elapsed.Add(fun (_event: EventArgs) -> pulse ())
        timer <- Some running
        running.Start()

let prepare () =
    RhinoApp.MainLoop.AddHandler main_loop
    Command.BeginCommand.AddHandler command_began

let shutdown () =
    shutting_down <- true
    stop HostInvalid
    RhinoApp.MainLoop.RemoveHandler main_loop
    Command.BeginCommand.RemoveHandler command_began

    match timer with
    | Some running ->
        running.Stop()
        running.Dispose()
        timer <- None
    | None -> ()

let start_session (view: RhinoView) (config: FlyConfig) (mode: FlightSessionMode) (entry: Entry) =
    finish_pending_exit ()

    if shutting_down then
        Error "RhinosCanFly is shutting down."
    elif not (entry.valid ()) then
        Ok()
    elif is_running () then
        stop ExplicitKeepCamera
        Ok()
    else
        try
            FlightBindingActions.validate config
            // Load/validate native entry before taking a camera snapshot.
            MacNative.load () |> ignore
            let host = PlatformInput.capture_viewport_host view

            if not (PlatformInput.viewport_host_is_foreground host view) then
                invalidOp "Mac navigation needs an active model viewport in the foreground Rhino window."

            if view.MouseCaptured false then
                invalidOp "Rhino still owns mouse capture in this viewport. Release it before starting navigation."

            if not (ViewportNameList.allows view.ActiveViewport.Name config.viewport_access.capabilities) then
                invalidOp "RhinosCanFly capabilities are disabled for this viewport."

            match entry.navigation with
            | Some ViewportNavigation.Operation.ParallelPan
            | Some ViewportNavigation.Operation.ParallelZoom when not view.ActiveViewport.IsParallelProjection ->
                invalidOp "The viewport is no longer using parallel projection."
            | _ -> ()

            if entry.held |> Option.exists (fun (held: unit -> bool) -> not (held ())) then
                invalidOp "The navigation entry button was released."

            let original_gumball = ModelAidSettings.AutoGumballEnabled
            let state = FlightState.create view host config mode

            let session =
                { state = state
                  input = InputAccumulator.create_with_capacity 8192
                  original_gumball = original_gumball
                  entry = entry
                  transport = None
                  wake = None
                  loop = None
                  crosshair = None
                  gumball_changed = false
                  lens_changed = false
                  inspected_at = 0L
                  entered = false
                  ending = false
                  finalizing = false }

            current <- Some session

            try
                let wake =
                    MacInputWake.create
                        (fun () ->
                            if
                                current
                                |> Option.exists (fun (active: Session) -> obj.ReferenceEquals(active, session))
                            then
                                pulse ())
                        (fun (error: exn) -> InputAccumulator.request_exit (SessionFailure error.Message) session.input)

                session.wake <- Some wake

                let transport =
                    MacNavigationInput.start
                        host.window
                        config
                        mode.lifetime
                        session.input
                        (Option.isNone entry.held)
                        entry.held_buttons
                        entry.entry_press
                        wake.Request
                        wake.RequestMotion

                session.transport <- Some transport

                ensure_scheduler ()

                if MacNavigationInput.complete_start transport = 1u then
                    activate session transport

                wake.Request()
                Ok()
            with
            | :? OperationCanceledException ->
                request_exit EntryCancelled session
                finish session
                ensure_scheduler ()
                Ok()
            | error ->
                request_exit (SessionFailure error.Message) session
                finish session
                ensure_scheduler ()
                Error error.Message
        with error ->
            Error error.Message

let queue_start
    (view: RhinoView)
    (config: FlyConfig)
    (mode: FlightSessionMode)
    (entry: Entry)
    (revision: int64 option)
    =
    let host = PlatformInput.capture_viewport_host view

    pending_start <-
        Some
            { view = view
              navigation = entry.navigation
              start =
                fun () ->
                    if
                        (revision |> Option.forall ((=) MacNavigationInput.lifecycle_revision))
                        && not shutting_down
                        && entry.valid ()
                        && PlatformInput.viewport_host_exists host view
                        && PlatformInput.viewport_host_is_foreground host view
                        && (entry.held |> Option.forall (fun (held: unit -> bool) -> held ()))
                    then
                        match start_session view config mode entry with
                        | Ok() -> ()
                        | Error error -> RhinoApp.WriteLine $"RhinosCanFly: {error}" }

    ensure_scheduler ()

let run_session (view: RhinoView) (config: FlyConfig) (mode: FlightSessionMode) (entry: Entry) =
    let toggled_off =
        match pending_start with
        | Some request -> obj.ReferenceEquals(request.view, view) && request.navigation = entry.navigation
        | None -> false

    pending_start <- None
    finish_pending_exit ()

    if shutting_down then
        Error "RhinosCanFly is shutting down."
    elif not (entry.valid ()) then
        Ok()
    elif
        not toggled_off
        && (current
            |> Option.exists (fun (session: Session) ->
                session.ending
                && (session.entry.navigation <> entry.navigation
                    || not (obj.ReferenceEquals(session.state.view, view)))))
    then
        queue_start view config mode entry None
        Ok()
    elif toggled_off || is_running () then
        stop ExplicitKeepCamera
        Ok()
    else
        match MacNavigationInput.complete_cleanup () with
        | Some error -> Error error
        | None when MacNavigationInput.pending_cleanup () ->
            queue_start view config mode entry (Some MacNavigationInput.lifecycle_revision)
            Ok()
        | None -> start_session view config mode entry

let run
    (view: RhinoView)
    (config: FlyConfig)
    (mode: FlightSessionMode)
    (held_entry: (unit -> bool) option)
    (held_buttons: uint32)
    (entry_press: MouseEntryPress option)
    (valid: unit -> bool)
    =
    let struct (mouse, context) = PlatformFlightKeyboard.take_entry ()

    run_session
        view
        config
        mode
        { flight_entry with
            held = held_entry
            held_buttons = held_buttons
            entry_press = entry_press
            valid = valid
            mouse_entry = mouse
            context = context }

let recovery_completed () = pulse ()
