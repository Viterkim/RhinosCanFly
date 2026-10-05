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
      started_at: float
      wheel_points_per_line: float
      lifetime: FlightLifetime
      exit_buttons: MouseExitConfig
      mutable active: bool
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

let mutable outside_event: MacNative.InputEvent -> uint32 =
    fun (_event: MacNative.InputEvent) -> 0u

let mutable raw_handler: RelativeMotion.Handler option = None

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
    match current with
    | Some session when session.active ->
        Volatile.Write(&session.active, false)
        PlatformFlightKeyboard.allow_passthrough ()

        let accepted =
            PlatformFlightKeyboard.resolve_pending_exit session.lifetime session.exit_buttons session.input
            |> Option.defaultValue reason

        InputAccumulator.request_exit accepted session.input
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
          key_down = false }
        session.input

let handle_event (event: MacNative.InputEvent) =
    let is_button = event.kind = 4u
    let is_key = event.kind = 3u || event.kind = 5u
    let code = if is_button then 128 + int event.code else int event.code
    let paired = (is_button || is_key) && code >= 0 && code < owned.Length
    let was_owned = paired && owned[code]

    try
        if event.kind = 6u then
            outside_event event |> ignore

        let held =
            if event.kind = 5u then
                PlatformFlightKeyboard.modifier_is_down code event.modifiers
            else
                event.down <> 0u

        // Consume owned Ups even after focus loss or flight exit.
        if was_owned && not held then
            owned[code] <- false

            match current with
            | Some session when
                session.active
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

                    PlatformFlightKeyboard.binding_exit session.lifetime session.exit_buttons session.input
                    |> Option.iter request_stop

                    if held && configured then 1u else 0u
                elif event.kind = 2u then
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
    MacNative.Handler(fun (event: byref<MacNative.InputEvent>) -> handle_event event)

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
            else
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
        then
            let result = (MacNative.load ()).end_monitor.Invoke()

            if result = 0 then
                installed_window <- 0n
                monitor_cleanup_pending <- false
                current <- None
            else
                cleanup_error <- Some $"AppKit monitor cleanup failed ({result})."

    cleanup_error

let start
    (window: nativeint)
    (config: FlyConfig)
    (lifetime: FlightLifetime)
    (input: InputAccumulator.State)
    (consume_entry_release: bool)
    =
    if current |> Option.exists (fun (session: Session) -> session.active) then
        invalidOp "Mac navigation is already active."

    complete_cleanup () |> Option.iter invalidOp

    if detached || hidden then
        invalidOp "The previous Mac pointer cleanup is unfinished."

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

    PlatformFlightKeyboard.configure config input offset entry_buttons
    next_session_id <- next_session_id + 1L

    let session =
        { id = next_session_id
          input = input
          window = window
          timestamp_offset = offset
          started_at = uptime
          wheel_points_per_line = MacNative.wheel_points_per_line ()
          lifetime = lifetime
          exit_buttons = config.mouse.exit_buttons
          active = false
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

        let result = MacNative.CGAssociateMouseAndMouseCursorPosition 0u

        if result <> 0 then
            failwith $"CoreGraphics could not disconnect the cursor ({result})."

        detached <- true

        let result = MacNative.CGDisplayHideCursor 0u

        if result <> 0 then
            failwith $"CoreGraphics could not hide the cursor ({result})."

        hidden <- true
        Volatile.Write(&session.active, true)

        let decoder = RelativeMotion.Decoder(ValueSome offset)

        let raw =
            RelativeMotion.Handler(fun (packet: byref<RelativeMotion.Packet>) ->
                try
                    if Volatile.Read(&session.active) then
                        let movement = decoder.Decode packet

                        if movement.dx <> 0L || movement.dy <> 0L then
                            InputAccumulator.add_timed_mouse movement.timestamp movement.dx movement.dy session.input
                with error ->
                    InputAccumulator.request_exit (SessionFailure error.Message) session.input)

        raw_handler <- Some raw

        if native.raw_begin.Invoke raw <= 0 then
            failwith
                "Mac raw input needs an available GCMouse device on macOS 14 or later. No accelerated input is used."

        session
    with _ ->
        request_stop (SessionFailure "Mac input startup failed.")
        PlatformFlightKeyboard.stop ()
        complete_cleanup () |> ignore
        reraise ()

let is_current (session: Session) =
    match current with
    | Some active ->
        obj.ReferenceEquals(active, session)
        && session.active
        && (MacNative.load ()).raw_available.Invoke() > 0u
    | None -> false

let pending_cleanup () =
    monitor_cleanup_pending
    || (installed_window <> 0n && not outside_enabled && not (Array.contains true owned))
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
