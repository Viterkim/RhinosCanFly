module RhinosCanFly.MacNavigationInput

open System
open System.Diagnostics
open System.Threading
open RhinosCanFly.Platform.Mac

type Session =
    { id: int64
      input: InputAccumulator.State
      window: nativeint
      timestamp_offset: int64
      mutable started_at: float
      config: FlyConfig
      entry_buttons: uint32
      held_buttons: uint32
      entry_press: MouseEntryPress option
      worker: bool
      decoder: RelativeMotion.Decoder
      wheel_points_per_line: float
      lifetime: FlightLifetime
      exit_buttons: MouseExitConfig
      notify: unit -> unit
      mutable active: bool
      mutable ready: bool
      mutable begun: bool
      mutable wheel_remainder: float }

let owned = Array.zeroCreate<bool> 133
let owned_session = Array.zeroCreate<int64> 133
let mutable next_session_id = 0L
let mutable current: Session option = None
let mutable installed_window = 0n
let mutable monitor_cleanup_pending = false
let mutable detached = false
let mutable hidden = false
let mutable cleanup_error: string option = None
let mutable outside_enabled = false
let mutable last_failure_session = 0L
let mutable last_failure_context = ""
let mutable failure_cleanup_pending = false

let mutable outside_event: MacNative.InputEvent -> uint32 =
    fun (_event: MacNative.InputEvent) -> 0u

let mutable raw_handler: RelativeMotion.Handler option = None
let mutable raw_notify: MacNative.Notify option = None
let mutable lifecycle_revision = 0L

let complete_start (session: Session) =
    let owned =
        match current with
        | Some active -> obj.ReferenceEquals(active, session)
        | None -> false

    if not owned || not session.active then
        0u
    elif session.ready then
        1u
    else
        let native = MacNative.load ()
        let state = native.raw_state.Invoke()

        if state = 1u then
            let result = MacNative.CGAssociateMouseAndMouseCursorPosition 0u

            if result <> 0 then
                failwith $"CoreGraphics could not disconnect the cursor ({result})."

            detached <- true

            let result = MacNative.CGDisplayHideCursor 0u

            if result <> 0 then
                failwith $"CoreGraphics could not hide the cursor ({result})."

            hidden <- true

            session.ready <- true

        state

let restore_pointer () =
    cleanup_error <- None

    if detached then
        let result = MacNative.CGAssociateMouseAndMouseCursorPosition 1u

        if result = 0 then
            detached <- false
        else
            cleanup_error <- Some $"CoreGraphics cursor association failed ({result})."

    if hidden then
        let result = MacNative.CGDisplayShowCursor 0u

        if result = 0 then
            hidden <- false
        else
            cleanup_error <- Some $"CoreGraphics cursor visibility failed ({result})."

let request_stop (reason: FlightExitReason) =
    lifecycle_revision <- lifecycle_revision + 1L

    match current with
    | Some session when session.active ->
        PlatformFlightKeyboard.exit_report <- $"exit={reason}"

        if
            reason <> EntryCancelled
            && not (FlightExitReason.is_explicit reason)
            && last_failure_session <> session.id
        then
            last_failure_session <- session.id

            last_failure_context <-
                $"last-managed-failure session={session.id} {PlatformFlightKeyboard.entry_report} exit={reason}"

            failure_cleanup_pending <- true
            PlatformFlightKeyboard.last_failure_report <- $"{last_failure_context} cleanup=pending"

        Volatile.Write(&session.active, false)
        PlatformFlightKeyboard.allow_passthrough ()

        if session.worker then
            (MacNative.load ()).raw_revoke.Invoke()

        let accepted =
            if session.worker then
                reason
            else
                PlatformFlightKeyboard.resolve_pending_exit session.lifetime session.exit_buttons session.input
                |> Option.defaultValue reason

        InputAccumulator.request_exit accepted session.input

        try
            session.notify ()
        with _ ->
            ()
    | _ -> ()

    restore_pointer ()

let enqueue
    (session: Session)
    (event: MacNative.InputEvent)
    (kind: InputAccumulator.TimelineEventKind)
    (dx: int64)
    (dy: int64)
    (wheel: int64)
    (button: RawMouseButtonTransition)
    =
    InputAccumulator.add_boundary_event
        { kind = kind
          timestamp =
            min
                (Stopwatch.GetTimestamp())
                (session.timestamp_offset + int64 (event.timestamp * float Stopwatch.Frequency))
          dx = dx
          dy = dy
          wheel = wheel
          button = button
          key = 0
          key_down = false
          terminal = false }
        session.input

let capture_config (session: Session) (mouse_entry: bool) (context: MacNative.InputEvent option) =
    let configured = Array.zeroCreate<byte> 133
    let command_keys = Array.zeroCreate<byte> 133
    let quarantine = Array.zeroCreate<byte> 133
    let appkit_owned = Array.zeroCreate<byte> 133
    let command_bindings = ResizeArray<KeyBinding>()

    let codes (code: int) =
        match code with
        | 36 -> [ 36; 52; 76 ]
        | 133 -> [ 56; 60 ]
        | 134 -> [ 59; 62 ]
        | 135 -> [ 58; 61 ]
        | 136 -> [ 55; 54 ]
        | code -> [ code ]

    FlightBindingActions.iter_bindings
        (fun (_name: string) (binding: KeyBinding) ->
            let command =
                binding.native_keys
                |> Array.exists (fun (code: int) -> code = 136 || code = 54 || code = 55)

            if command then
                command_bindings.Add binding

            for key in binding.native_keys do
                for code in codes key do
                    configured[code] <- 1uy

                    if command then
                        command_keys[code] <- 1uy)
        session.config

    for code = 128 to 132 do
        configured[code] <- 1uy

    for code = 0 to 132 do
        if owned[code] then
            appkit_owned[code] <- 1uy

        if not mouse_entry then
            quarantine[code] <-
                match context with
                | Some event when
                    int event.code = code
                    || PlatformFlightKeyboard.modifier_is_down code event.modifiers
                    || ((code = 56 || code = 60) && event.modifiers &&& (1UL <<< 17) <> 0UL)
                    || ((code = 59 || code = 62) && event.modifiers &&& (1UL <<< 18) <> 0UL)
                    || ((code = 58 || code = 61) && event.modifiers &&& (1UL <<< 19) <> 0UL)
                    || ((code = 54 || code = 55) && event.modifiers &&& (1UL <<< 20) <> 0UL)
                    ->
                    1uy
                | None -> configured[code]
                | _ -> 0uy

    let terminal =
        Array.init 32 (fun (_index: int) ->
            let mutable binding = Unchecked.defaultof<MacNative.CaptureBinding>
            binding.keys <- Array.zeroCreate 16
            binding)

    let command =
        Array.init 32 (fun (_index: int) ->
            let mutable binding = Unchecked.defaultof<MacNative.CaptureBinding>
            binding.keys <- Array.zeroCreate 16
            binding)

    for index = 0 to command_bindings.Count - 1 do
        let binding = command_bindings[index]

        if binding.native_keys.Length > 16 then
            invalidOp "A Mac Command binding has too many keys."

        command[index].count <- uint32 binding.native_keys.Length
        Array.blit (Array.map uint32 binding.native_keys) 0 command[index].keys 0 binding.native_keys.Length

    for index, binding in
        [ 0, session.config.bindings.exit_key
          1, session.config.bindings.cancel_flight_and_restore ] do
        if binding.native_keys.Length > 16 then
            invalidOp "A Mac terminal binding has too many keys."

        terminal[index].count <- uint32 binding.native_keys.Length
        Array.blit (Array.map uint32 binding.native_keys) 0 terminal[index].keys 0 binding.native_keys.Length

    let buttons = session.exit_buttons

    let exit_buttons =
        [ buttons.left
          buttons.right || session.lifetime = FlightLifetime.WhileRightMouseHeld
          buttons.middle
          buttons.mouse4
          buttons.mouse5 ]
        |> List.mapi (fun (index: int) (enabled: bool) -> if enabled then 1u <<< index else 0u)
        |> List.fold (|||) 0u

    let mutable capture = Unchecked.defaultof<MacNative.CaptureConfig>
    capture.session <- uint64 session.id
    capture.exit_buttons <- exit_buttons
    capture.entry_buttons <- session.entry_buttons
    capture.command_count <- uint32 command_bindings.Count
    capture.terminal_count <- 2u
    capture.configured <- configured
    capture.command_keys <- command_keys
    capture.quarantine <- quarantine
    capture.appkit_owned <- appkit_owned
    capture.terminal <- terminal
    capture.command <- command

    capture.held_buttons <- session.held_buttons
    capture.entry_mouse_button <- UInt32.MaxValue

    match session.entry_press with
    | Some press ->
        capture.entry_mouse_button <- uint32 press.button
        capture.entry_mouse_press_id <- press.id
        capture.entry_mouse_source_time <- press.source_time
    | None -> ()

    match context with
    | Some event ->
        capture.entry_code <- event.code
        capture.entry_source_time <- event.source_time
        capture.entry_modifiers <- event.modifiers
    | None -> ()

    capture

let activate (session: Session) (mouse_entry: bool) (context: MacNative.InputEvent option) =
    if session.worker then
        PlatformFlightKeyboard.long_pauses <- 0
        PlatformFlightKeyboard.camera_publications <- 0
        PlatformFlightKeyboard.redraws <- 0
        PlatformFlightKeyboard.pointer_discards <- 0
        PlatformFlightKeyboard.exit_report <- "exit=none"
        PlatformFlightKeyboard.cleanup_report <- "cleanup=pending"

        PlatformFlightKeyboard.entry_report <-
            match mouse_entry, context with
            | true, _ -> "entry=mouse"
            | _, Some event -> $"entry=shortcut code={event.code} flags={event.modifiers:x} source={event.source_time}"
            | _ -> "entry=command-fallback"

        let mutable capture = capture_config session mouse_entry context

        if (MacNative.load ()).raw_activate.Invoke(&capture, session.window) <> 0 then
            invalidOp "The Mac capture session could not activate."

let handle_native (event: MacNative.InputEvent) =
    match current with
    | Some session when session.active && event.session = uint64 session.id ->
        let timestamp =
            session.timestamp_offset + int64 (event.timestamp * float Stopwatch.Frequency)

        match event.kind with
        | 9u ->
            let kind =
                match event.code with
                | 0u -> InputAccumulator.TimelineEventKind.ExitKeepCamera
                | 1u -> InputAccumulator.TimelineEventKind.ExitRestoreCamera
                | 2u -> InputAccumulator.TimelineEventKind.ExitHeldRelease
                | 3u -> InputAccumulator.TimelineEventKind.ExitCancelledEntry
                | reason ->
                    let message = $"Mac input bridge returned an unknown End reason ({reason})."
                    request_stop (SessionFailure message)
                    invalidOp message

            enqueue session event kind 0L 0L 0L Unchecked.defaultof<_>
        | 8u ->
            let native = MacNative.load ()
            session.begun <- true
            session.started_at <- event.timestamp

            PlatformFlightKeyboard.configure
                session.config
                session.input
                session.timestamp_offset
                session.entry_buttons
                false
                (fun (code: int) -> native.raw_initial_key.Invoke(uint32 code) <> 0u)

            enqueue session event InputAccumulator.TimelineEventKind.BeginSession 0L 0L 0L Unchecked.defaultof<_>
        | 1u ->
            let mutable packet = Unchecked.defaultof<RelativeMotion.Packet>
            packet.timestamp <- event.timestamp
            packet.dx <- event.dx
            packet.dy <- event.dy
            let movement = session.decoder.Decode packet
            InputAccumulator.add_timed_mouse movement.timestamp movement.dx movement.dy session.input
        | 3u
        | 5u ->
            PlatformFlightKeyboard.observe_event
                timestamp
                (int event.code)
                (event.down <> 0u)
                (event.reserved &&& 2u <> 0u)
        | 4u ->
            PlatformFlightKeyboard.observe_mouse_event
                timestamp
                { event = enum<RawMouseButtonEvent> (2 * int event.code + (if event.down <> 0u then 1 else 2))
                  modifiers =
                    { shift = event.navigation_modifiers &&& (1UL <<< 17) <> 0UL
                      control = event.navigation_modifiers &&& (1UL <<< 18) <> 0UL
                      alt = event.navigation_modifiers &&& (1UL <<< 19) <> 0UL } }
                (event.reserved &&& 2u <> 0u)
        | 2u ->
            let scale =
                if event.precise <> 0u then
                    120. / session.wheel_points_per_line
                else
                    120.

            let wheel = session.wheel_remainder + event.wheel * scale

            if not (Double.IsFinite wheel) || abs wheel >= 4503599627370496. then
                invalidOp "The native mouse source returned an invalid wheel delta."

            let delta = int64 wheel
            session.wheel_remainder <- wheel - float delta

            if delta <> 0L then
                enqueue session event InputAccumulator.TimelineEventKind.Wheel 0L 0L delta Unchecked.defaultof<_>
        | _ -> invalidOp "The native input batch contains an unknown record."

        1u
    | _ -> 0u

let handle_event (event: MacNative.InputEvent) =
    let is_button = event.kind = 4u
    let is_key = event.kind = 3u || event.kind = 5u
    let code = if is_button then 128 + int event.code else int event.code
    let paired = (is_button || is_key) && code >= 0 && code < owned.Length
    let was_owned = paired && owned[code]

    try
        current
        |> Option.iter (fun (session: Session) -> complete_start session |> ignore)

        if event.kind = 6u then
            request_stop FocusLost
            outside_event event |> ignore

        let held =
            if event.kind = 5u then
                PlatformFlightKeyboard.modifier_is_down code event.modifiers
            else
                event.down <> 0u

        // Consume owned Ups even after focus loss or flight exit.
        if event.reserved &&& 0x80000000u <> 0u then
            handle_native event
        elif was_owned && not held then
            owned[code] <- false

            match current with
            | Some session when
                session.active
                && not session.worker
                && event.timestamp >= session.started_at
                && (owned_session[code] = session.id || owned_session[code] = 0L)
                ->
                if is_button then
                    let transition =
                        { event = enum<RawMouseButtonEvent> (2 * (code - 128) + 2)
                          modifiers = MouseModifiers.none }

                    PlatformFlightKeyboard.observe_mouse
                        (min
                            (Stopwatch.GetTimestamp())
                            (session.timestamp_offset + int64 (event.timestamp * float Stopwatch.Frequency)))
                        transition
                else
                    PlatformFlightKeyboard.observe
                        (session.timestamp_offset + int64 (event.timestamp * float Stopwatch.Frequency))
                        code
                        false

                PlatformFlightKeyboard.binding_exit session.lifetime session.exit_buttons session.input
                |> Option.iter request_stop
            | _ -> ()

            if owned_session[code] = 0L then
                outside_event event |> ignore

            1u
        elif was_owned && event.repeated <> 0u then
            1u
        else
            if was_owned then
                owned[code] <- false

            match current with
            | Some session when session.active && session.worker ->
                if
                    MacNative.foreground_window () <> session.window
                    || (event.window <> 0n && event.window <> session.window)
                then
                    request_stop FocusLost

                0u
            | Some session when session.active ->
                if
                    MacNative.foreground_window () <> session.window
                    || (event.window <> 0n && event.window <> session.window)
                then
                    request_stop FocusLost
                    0u
                elif event.kind = 6u then
                    request_stop FocusLost
                    0u
                elif event.timestamp < session.started_at then
                    0u
                elif paired then
                    let configured = is_button || PlatformFlightKeyboard.configured_key code

                    if held && configured && event.repeated = 0u then
                        owned[code] <- true
                        owned_session[code] <- session.id

                    if is_button && configured then
                        let transition =
                            { event = enum<RawMouseButtonEvent> (2 * (code - 128) + (if held then 1 else 2))
                              modifiers =
                                { shift = event.modifiers &&& (1UL <<< 17) <> 0UL
                                  control = event.modifiers &&& (1UL <<< 18) <> 0UL
                                  alt = event.modifiers &&& (1UL <<< 19) <> 0UL } }

                        PlatformFlightKeyboard.observe_mouse
                            (min
                                (Stopwatch.GetTimestamp())
                                (session.timestamp_offset + int64 (event.timestamp * float Stopwatch.Frequency)))
                            transition
                    elif configured || not held then
                        PlatformFlightKeyboard.observe
                            (session.timestamp_offset + int64 (event.timestamp * float Stopwatch.Frequency))
                            code
                            held

                    (if session.ready then
                         PlatformFlightKeyboard.binding_exit session.lifetime session.exit_buttons session.input
                     else
                         PlatformFlightKeyboard.resolve_pending_exit session.lifetime session.exit_buttons session.input)
                    |> Option.iter request_stop

                    if held && configured then 1u else 0u
                elif event.kind = 2u && session.ready then
                    // AppKit already applies the user's natural-scroll setting.
                    let scale =
                        if event.precise <> 0u then
                            120. / session.wheel_points_per_line
                        else
                            120.

                    let wheel = session.wheel_remainder + event.wheel * scale

                    if Double.IsNaN wheel || Double.IsInfinity wheel || abs wheel >= 4503599627370496. then
                        invalidOp "The native mouse source returned an invalid wheel delta."

                    let delta = int64 wheel
                    session.wheel_remainder <- wheel - float delta

                    if delta <> 0L then
                        enqueue
                            session
                            event
                            InputAccumulator.TimelineEventKind.Wheel
                            0L
                            0L
                            delta
                            Unchecked.defaultof<_>

                    1u
                else
                    0u
            | _ when outside_enabled && event.kind <> 6u ->
                let consumed = outside_event event

                if consumed <> 0u && paired && held then
                    owned[code] <- true
                    owned_session[code] <- 0L

                consumed
            | _ -> 0u
    with error ->
        try
            request_stop (SessionFailure error.Message)
        with cleanup ->
            cleanup_error <- Some cleanup.Message

        if paired && (was_owned || owned[code]) then 1u else 0u

let handler =
    MacNative.Handler(fun (event: byref<MacNative.InputEvent>) ->
        let session = current

        try
            handle_event event
        finally
            match session with
            | Some active when event.reserved &&& 0x80000000u = 0u ->
                try
                    active.notify ()
                with error ->
                    InputAccumulator.request_exit (SessionFailure error.Message) active.input
            | _ -> ())

let ensure_monitor () =
    if monitor_cleanup_pending then
        let result = (MacNative.load ()).end_monitor.Invoke()

        if result <> 0 then
            failwith $"AppKit monitor cleanup failed ({result})."

        monitor_cleanup_pending <- false
        installed_window <- 0n

    if installed_window = 0n then
        let result = (MacNative.load ()).begin_monitor.Invoke(handler, 0n)

        if result = 5 then
            installed_window <- -1n
            monitor_cleanup_pending <- true

        if result <> 0 then
            failwith $"AppKit refused input monitoring ({result})."

        installed_window <- -1n

let enable_outside (enabled: bool) =
    outside_enabled <- enabled

    if enabled then
        ensure_monitor ()

let complete_cleanup () =
    if current |> Option.forall (fun (session: Session) -> not session.active) then
        restore_pointer ()

        if Option.isSome raw_handler then
            let result = (MacNative.load ()).raw_end.Invoke()

            if result = 0 then
                raw_handler <- None
                raw_notify <- None
            elif result <> -1 then
                cleanup_error <- Some $"Mac raw-input cleanup failed ({result})."

        if installed_window <> 0n then
            let result = (MacNative.load ()).monitor_window.Invoke(0n)

            if result <> 0 then
                cleanup_error <- Some $"AppKit window cleanup failed ({result})."

        if
            not detached
            && not hidden
            && Option.isNone raw_handler
            && Option.isNone cleanup_error
        then
            current <- None

        if
            installed_window <> 0n
            && (not outside_enabled || monitor_cleanup_pending)
            && not detached
            && not hidden
            && Option.isNone raw_handler
            && Option.isNone cleanup_error
            && not (Array.contains true owned)
            && (MacNative.load ()).raw_guards_pending.Invoke() = 0u
        then
            let result = (MacNative.load ()).end_monitor.Invoke()

            if result = 0 then
                installed_window <- 0n
                monitor_cleanup_pending <- false
                current <- None
            else
                cleanup_error <- Some $"AppKit monitor cleanup failed ({result})."

    PlatformFlightKeyboard.cleanup_report <-
        match cleanup_error with
        | Some error -> $"cleanup={error}"
        | None when Option.isSome raw_handler || hidden || detached -> "cleanup=pending"
        | None -> "cleanup=complete"

    if failure_cleanup_pending then
        PlatformFlightKeyboard.last_failure_report <- $"{last_failure_context} {PlatformFlightKeyboard.cleanup_report}"

        if Option.isNone raw_handler && not hidden && not detached then
            failure_cleanup_pending <- false

    cleanup_error

let start
    (window: nativeint)
    (config: FlyConfig)
    (lifetime: FlightLifetime)
    (input: InputAccumulator.State)
    (consume_entry_release: bool)
    (held_buttons: uint32)
    (entry_press: MouseEntryPress option)
    (notify: unit -> unit)
    (notify_motion: unit -> unit)
    =
    if current |> Option.exists (fun (session: Session) -> session.active) then
        invalidOp "Mac navigation is already active."

    complete_cleanup () |> Option.iter invalidOp

    if detached || hidden || Option.isSome raw_handler then
        invalidOp "The previous Mac pointer cleanup is unfinished."

    let backend = 3u

    for code = 128 to owned.Length - 1 do
        if
            PlatformBindings.physical_key_down code
            && not (owned[code] && owned_session[code] = 0L)
        then
            invalidOp "Release the mouse buttons before starting navigation."

    let native = MacNative.load ()
    let uptime = native.uptime.Invoke()
    let offset = Stopwatch.GetTimestamp() - int64 (uptime * float Stopwatch.Frequency)
    let mutable entry_buttons = 0u

    if consume_entry_release then
        for code = 128 to owned.Length - 1 do
            if owned[code] && owned_session[code] = 0L then
                entry_buttons <- entry_buttons ||| (1u <<< (code - 128))

    PlatformFlightKeyboard.configure
        config
        input
        offset
        entry_buttons
        false
        (if backend = 3u then
             (fun (_code: int) -> false)
         else
             PlatformBindings.physical_key_down)

    PlatformFlightKeyboard.worker_timeline <- backend = 3u
    next_session_id <- next_session_id + 1L

    let session =
        { id = next_session_id
          input = input
          window = window
          timestamp_offset = offset
          started_at = uptime
          config = config
          entry_buttons = entry_buttons
          held_buttons = held_buttons
          entry_press = entry_press
          worker = backend = 3u
          decoder = RelativeMotion.Decoder(ValueSome offset)
          wheel_points_per_line = MacNative.wheel_points_per_line ()
          lifetime = lifetime
          exit_buttons = config.mouse.exit_buttons
          notify = notify
          active = false
          ready = false
          begun = false
          wheel_remainder = 0. }

    current <- Some session

    try
        for code = 0 to 127 do
            if owned[code] && PlatformBindings.physical_key_down code then
                owned_session[code] <- session.id

        ensure_monitor ()
        let result = native.monitor_window.Invoke window

        if result <> 0 then
            failwith $"AppKit could not prepare the document window ({result})."

        Volatile.Write(&session.active, true)

        let decoder = RelativeMotion.Decoder(ValueSome offset)

        let raw =
            RelativeMotion.Handler(fun (packet: byref<RelativeMotion.Packet>) ->
                try
                    if Volatile.Read(&session.active) then
                        let movement = decoder.Decode packet

                        if movement.dx <> 0L || movement.dy <> 0L then
                            InputAccumulator.add_timed_mouse movement.timestamp movement.dx movement.dy session.input
                            notify_motion ()
                with error ->
                    InputAccumulator.request_exit (SessionFailure error.Message) session.input

                    try
                        session.notify ()
                    with _ ->
                        ())

        raw_handler <- Some raw

        let notification =
            MacNative.Notify(fun () ->
                try
                    if Volatile.Read(&session.active) then
                        notify_motion ()
                with error ->
                    InputAccumulator.request_exit (SessionFailure error.Message) session.input)

        raw_notify <- Some notification

        if native.raw_begin.Invoke(raw, notification, backend) <= 0 then
            if backend = 3u then
                failwith "Mac input capture could not start. Check Rhino's Accessibility permission."
            else
                failwith "Mac unaccelerated pointer input could not start."

        complete_start session |> ignore
        session
    with error ->
        request_stop (SessionFailure error.Message)
        PlatformFlightKeyboard.stop ()
        complete_cleanup () |> ignore
        reraise ()

let is_current (session: Session) =
    match current with
    | Some active ->
        obj.ReferenceEquals(active, session)
        && session.active
        && session.ready
        && (MacNative.load ()).raw_available.Invoke() > 0u
    | None -> false

let pending_cleanup () =
    monitor_cleanup_pending
    || (installed_window <> 0n
        && not outside_enabled
        && not (Array.contains true owned)
        && (MacNative.load ()).raw_guards_pending.Invoke() = 0u)
    || detached
    || hidden
    || Option.isSome raw_handler
    || Option.isSome cleanup_error

let stop (session: Session) (reason: FlightExitReason) =
    if
        current
        |> Option.exists (fun (active: Session) -> obj.ReferenceEquals(active, session))
    then
        request_stop reason
        PlatformFlightKeyboard.stop ()

    complete_cleanup ()
