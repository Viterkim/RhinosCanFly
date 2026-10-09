// Feed production C traces through the compiled Mac handler and flight loop.
open System
open System.Diagnostics
open System.IO
open System.Runtime.InteropServices
open Microsoft.FSharp.Reflection
open Rhino.Geometry
open RhinosCanFly
open RhinosCanFly.Platform.Mac

let mutable checks = 0

let check (name: string) (expected: 'T) (actual: 'T) =
    checks <- checks + 1

    if actual <> expected then
        failwithf "%s: expected %A, got %A" name expected actual

let config =
    match ConfigCompiler.compile ConfigSchema.defaults with
    | Ok config -> config
    | Error error -> failwith error

let make_record<'T> (values: (string * obj) list) =
    let fields = FSharpType.GetRecordFields typeof<'T>

    fields
    |> Array.map (fun (field: System.Reflection.PropertyInfo) ->
        match List.tryFind (fun (name: string, _value: obj) -> name = field.Name) values with
        | Some(_, value) -> value
        | None when field.PropertyType.IsValueType -> Activator.CreateInstance field.PropertyType
        | None -> null)
    |> fun (values: obj array) -> FSharpValue.MakeRecord(typeof<'T>, values) :?> 'T

let state_with (config: FlyConfig) =
    let camera: CameraState =
        { position = Point3d.Origin
          target = Point3d(0., 1., 0.)
          direction = Vector3d.YAxis
          up = Vector3d.ZAxis }

    make_record<FlyState>
        [ "config", box config
          "session_mode", box (FlightSessionMode.until_exit FlightMode.Normal)
          "camera_write_allowed", box (fun () -> true)
          "active_mouse_navigation", box MouseLook
          "latched_mouse_navigation", box LookNavigation
          "projection", box ViewProjectionKind.Perspective
          "speed", box 1.
          "camera", box camera ]

let state () = state_with config

let run_using
    (config: FlyConfig)
    (initial: byte array)
    (error: uint32)
    (active: bool)
    (records: MacNative.InputEvent array)
    (partition: int)
    (verify: FlyState -> unit)
    =
    let input = InputAccumulator.create_with_capacity 8192
    let state = state_with config

    let session: MacNavigationInput.Session =
        { id = 1L
          input = input
          window = 0n
          timestamp_offset = 0L
          started_at = 1.
          config = config
          entry_buttons = 0u
          held_buttons = 0u
          entry_press = None
          worker = true
          decoder = RelativeMotion.Decoder(ValueSome 0L)
          wheel_points_per_line = 10.
          lifetime = FlightLifetime.UntilExit
          exit_buttons = config.mouse.exit_buttons
          notify = ignore
          active = true
          ready = true
          begun = false
          wheel_remainder = 0. }

    MacNative.api <-
        Some(
            make_record<MacNative.Api>
                [ "raw_initial_key", box (MacNative.InitialKey(fun (code: uint32) -> uint32 initial[int code]))
                  "raw_revoke", box (MacNative.Discard(ignore)) ]
        )

    MacNavigationInput.current <- Some session
    PlatformFlightKeyboard.worker_timeline <- true
    let mutable frontier = int64 Stopwatch.Frequency
    let mutable publications = 0
    let mutable navigation_transitions = 0
    let mutable target_requests = 0

    let host: FlightLoop.Host =
        { timestamp = fun () -> frontier
          elapsed_seconds = fun () -> float frontier / float Stopwatch.Frequency - 1.
          movement_boundary = fun () -> struct (frontier + Stopwatch.Frequency / 4L, frontier)
          inspect =
            fun (seconds: float) ->
                state.next_host_validation_at <- seconds + 0.1

                InputAccumulator.exit_reason input
                |> Option.iter (fun (reason: FlightExitReason) -> FlyState.request_exit reason state)
          publish =
            fun (change: ViewChange) ->
                if change <> ViewChange.none then
                    check "publication precedes lifecycle exit" true (FlyState.is_running state)
                    publications <- publications + 1
                    true
                else
                    false
          update_navigation =
            fun () ->
                let changed =
                    FlightCamera.update_navigation_mode_with_host
                        (fun (_mode: ViewNavigationMode) ->
                            target_requests <- target_requests + 1
                            Point3d(0., 10., 0.))
                        (fun () -> FlyState.is_running state)
                        state

                if changed then
                    navigation_transitions <- navigation_transitions + 1

                changed
          redraw = ignore
          pivot_target = fun () -> state.key_pivot_target }

    let loop = FlightLoop.create_core host (fun () -> Ok true) ignore input state
    let mutable delivered = 0

    for batch in records |> Array.chunkBySize partition do
        for event in batch do
            check "production handler accepts native session" 1u (MacNavigationInput.handle_native event)

        delivered <- delivered + batch.Length

        let boundary =
            if delivered < records.Length then
                records[delivered].timestamp
            else
                batch[batch.Length - 1].timestamp

        frontier <- int64 (boundary * float Stopwatch.Frequency)
        loop.step ()

        if FlyState.is_running state then
            check "frozen native frontier leaves loop idle" false (loop.work_pending ())

    if error <> 0u then
        MacNavigationInput.request_stop (FlightSession.capture_failure error)
        loop.step ()
        check "authority failure revokes transport" false session.active

    verify state

    check "native and managed lifecycle agree" active (FlyState.is_running state)
    MacNavigationInput.current <- None
    PlatformFlightKeyboard.stop ()
    check "cleanup releases final key state" false (PlatformFlightKeyboard.key_is_down 13)
    struct (state.camera.position.Y, publications, navigation_transitions, target_requests)

let run (initial: byte array) (error: uint32) (active: bool) (records: MacNative.InputEvent array) (partition: int) =
    let struct (distance, publications, _, _) =
        run_using config initial error active records partition ignore

    struct (distance, publications)

let read_traces (trace_path: string) =
    use stream = File.OpenRead trace_path
    use reader = new BinaryReader(stream)
    check "native trace ABI" 0x52434635u (reader.ReadUInt32())
    let scenarios = int (reader.ReadUInt32())

    for _scenario = 1 to scenarios do
        let scenario = reader.ReadUInt32()
        let initial = reader.ReadBytes 133
        let error = reader.ReadUInt32()
        let active = reader.ReadUInt32() <> 0u
        let count = int (reader.ReadUInt32())
        let size = Marshal.SizeOf<MacNative.InputEvent>()
        let memory = Marshal.AllocHGlobal size

        let records =
            try
                Array.init count (fun (_index: int) ->
                    let bytes = reader.ReadBytes size
                    check "complete native record" size bytes.Length
                    Marshal.Copy(bytes, 0, memory, size)
                    Marshal.PtrToStructure<MacNative.InputEvent> memory)
            finally
                Marshal.FreeHGlobal memory

        let expected = run initial error active records 1
        let struct (expected_distance, _) = expected

        for partition in [ 2; 64; 8192 ] do
            let struct (distance, publications) = run initial error active records partition
            check "batch partition preserves replay and exit" true (abs (distance - expected_distance) < 1e-10)

            if scenario = 4u then
                check "ordinary terminal publishes once across partitions" 1 publications

        if scenario = 4u then
            let struct (distance, publications) = expected
            check "20 ms accepted movement survives ordered exit" true (abs (distance - 0.02) < 1e-10)
            check "ordinary terminal publishes once" 1 publications

        if scenario = 5u || scenario = 8u then
            let struct (distance, _) = expected
            check "sampled movement starts at Begin" true (abs (distance - 0.03) < 1e-10)

        if scenario = 9u then
            check
                "unexpected host press cancels admission"
                true
                (records
                 |> Array.exists (fun (event: MacNative.InputEvent) -> event.kind = 9u && event.code = 3u))

    check "native trace has no trailing data" stream.Length stream.Position

let managed_smoke () =
    let event (kind: uint32) (code: uint32) (down: uint32) (timestamp: float) =
        let mutable event = Unchecked.defaultof<MacNative.InputEvent>
        event.kind <- kind
        event.code <- code
        event.down <- down
        event.timestamp <- timestamp
        event.session <- 1UL
        event

    let records =
        [| event 8u 0u 0u 1.
           event 3u 13u 1u 1.01
           event 3u 13u 0u 1.03
           event 9u 0u 0u 1.04 |]

    for partition in [ 1; 2; 64; 8192 ] do
        let struct (distance, publications) =
            run (Array.zeroCreate 133) 0u false records partition

        check "managed ordered replay keeps short movement" true (abs (distance - 0.02) < 1e-10)
        check "managed ordered replay publishes once" 1 publications

    let initial = Array.zeroCreate 133
    let failure = run initial 14u false Array.empty 1
    check "host gesture revokes before any flight movement" (struct (0., 0)) failure

    for partition in [ 1; 2; 64; 8192 ] do
        let initial = Array.zeroCreate 133
        initial[13] <- 1uy

        let records =
            [| event 8u 0u 0u 1.
               event 3u 13u 1u 1.01
               event 3u 13u 0u 1.03
               event 9u 0u 0u 1.04 |]

        let struct (distance, _) = run initial 0u false records partition
        check "sampled held movement reconciles first delivered Down" true (abs (distance - 0.03) < 1e-10)

        let records = [| event 8u 0u 0u 1.; event 9u 3u 0u 1. |]

        run_using config (Array.zeroCreate 133) 0u false records partition (fun (state: FlyState) ->
            check "already released entry terminates through End" (Some EntryCancelled) state.exit_reason
            check "cancelled entry restores preparation" true state.restore_camera_on_exit
            check "cancelled entry skips normal exit actions" false (FlightExitReason.is_explicit EntryCancelled))
        |> ignore

    for terminal, ending in [ "Escape", 0u; "T", 1u; "MouseX1", 0u ] do
        let file =
            { ConfigSchema.defaults with
                exit_key = if ending = 0u then terminal else "Escape"
                cancel_flight_and_restore = if ending = 1u then terminal else "T"
                toggle_projection = terminal
                speed_increase = terminal
                retarget_all_views = terminal
                mouse4_action_while_flying = true
                mouse4_action = MouseGestureAction.Retarget }

        let overlapping =
            match ConfigCompiler.compile file with
            | Ok config -> config
            | Error error -> failwith error

        let kind, code =
            if terminal = "MouseX1" then 4u, 3u
            elif ending = 1u then 3u, 17u
            else 3u, 53u

        let records =
            [| event 8u 0u 0u 1.
               event 3u 13u 1u 1.01
               event kind code 1u 1.03
               event 9u ending 0u 1.03 |]

        for partition in [ 1; 2; 3; 64; 8192 ] do
            run_using overlapping (Array.zeroCreate 133) 0u false records partition (fun (state: FlyState) ->
                check "terminal suppresses projection" ViewProjectionKind.Perspective state.projection
                check "terminal suppresses speed" 1. state.speed

                check
                    "terminal keeps ordered reason"
                    (Some(
                        if ending = 1u then
                            ExplicitRestoreCamera
                        else
                            ExplicitKeepCamera
                    ))
                    state.exit_reason

                check "accepted movement precedes terminal" true (abs (state.camera.position.Y - 0.02) < 1e-10))
            |> ignore

    let lower =
        InputAccumulator.KeyboardAction.ProjectionToggle
        ||| InputAccumulator.KeyboardAction.SpeedIncrease
        ||| InputAccumulator.KeyboardAction.RetargetAllViews

    for terminal in
        [ InputAccumulator.KeyboardAction.Exit
          InputAccumulator.KeyboardAction.CancelAndRestore ] do
        check
            "Windows terminal arbitration keeps priority"
            terminal
            (FlightBindingActions.arbitrate_terminal false (terminal ||| lower))

        check
            "Mac terminal arbitration defers completion only"
            InputAccumulator.KeyboardAction.DeferredExit
            (FlightBindingActions.arbitrate_terminal true (terminal ||| lower))

    let toggle_config =
        { config with
            bindings =
                { config.bindings with
                    toggle_projection = Some config.bindings.forward } }

    let initial = Array.zeroCreate 133
    initial[13] <- 1uy

    let records =
        [| event 8u 0u 0u 1.
           event 3u 13u 1u 1.01
           event 3u 13u 0u 1.02
           event 9u 0u 0u 1.03 |]

    run_using toggle_config initial 0u false records 1 (fun (state: FlyState) ->
        check "sampled held key never manufactures toggle" ViewProjectionKind.Perspective state.projection)
    |> ignore

    let inherited_config =
        { toggle_config with
            bindings =
                { toggle_config.bindings with
                    speed_increase = Some config.bindings.forward } }

    let repeats =
        [| for index in 1..4 do
               let mutable repeat = event 3u 13u 1u (1. + float index * 0.005)
               repeat.repeated <- 1u
               yield repeat |]

    let records =
        Array.concat
            [ [| event 8u 0u 0u 1. |]
              repeats
              [| event 3u 13u 0u 1.03; event 9u 0u 0u 1.04 |] ]

    for partition in [ 1; 2; 4; 64; 8192 ] do
        let struct (distance, _, _, _) =
            run_using inherited_config initial 0u false records partition (fun (state: FlyState) ->
                check "inherited repeats do not toggle projection" ViewProjectionKind.Perspective state.projection
                check "inherited repeats do not apply speed actions" 1. state.speed)

        check "inherited repeats preserve movement until real Up" true (abs (distance - 0.03) < 1e-10)

    let inherited_input = InputAccumulator.create ()

    let inherited_session =
        make_record<MacNavigationInput.Session>
            [ "id", box 1L
              "config", box inherited_config
              "input", box inherited_input
              "active", box true
              "worker", box true ]

    MacNative.api <-
        Some(
            make_record<MacNative.Api>
                [ ("raw_initial_key", box (MacNative.InitialKey(fun (code: uint32) -> uint32 initial[int code]))) ]
        )

    MacNavigationInput.current <- Some inherited_session
    PlatformFlightKeyboard.worker_timeline <- true
    MacNavigationInput.handle_native (event 8u 0u 0u 1.) |> ignore
    let timeline = InputAccumulator.timeline_buffer_for inherited_input
    InputAccumulator.drain_timeline timeline inherited_input |> ignore
    let mutable action_count = 0

    for repeat in repeats do
        MacNavigationInput.handle_native repeat |> ignore

        if
            PlatformFlightKeyboard.collect_actions ()
            <> InputAccumulator.KeyboardAction.None
        then
            action_count <- action_count + 1

    let struct (count, overflow) =
        InputAccumulator.drain_timeline timeline inherited_input

    check "inherited repeats enqueue no fresh action transitions" 0 count
    check "inherited repeats do not overflow replay" false overflow
    check "inherited repeats preserve logical movement" true (PlatformFlightKeyboard.key_is_down 13)
    MacNavigationInput.handle_native (event 3u 13u 0u 1.03) |> ignore
    let struct (count, _) = InputAccumulator.drain_timeline timeline inherited_input
    check "inherited real release enqueues once" 1 count

    if
        PlatformFlightKeyboard.apply_keyboard_transition timeline[0].key timeline[0].key_down
        <> InputAccumulator.KeyboardAction.None
    then
        action_count <- action_count + 1

    check "inherited press and repeats manufacture zero actions" 0 action_count
    check "inherited real release clears logical movement" false (PlatformFlightKeyboard.key_is_down 13)
    MacNavigationInput.current <- None
    PlatformFlightKeyboard.stop ()

    let session =
        make_record<MacNavigationInput.Session>
            [ "id", box 1L
              "config", box config
              "held_buttons", box 8u
              "exit_buttons", box config.mouse.exit_buttons ]

    let capture = MacNavigationInput.capture_config session true None
    check "entry requirement survives cleared mouse ownership" 8u capture.held_buttons

    for name, offset in
        [ "session", 0
          "window", 8
          "exit_buttons", 12
          "terminal_count", 16
          "entry_buttons", 20
          "command_count", 24
          "configured", 28
          "command_keys", 161
          "quarantine", 294
          "appkit_owned", 427
          "terminal", 560
          "command", 2736
          "held_buttons", 4912
          "entry_code", 4916
          "entry_source_time", 4920
          "entry_modifiers", 4928
          "entry_mouse_button", 4936
          "entry_mouse_press_id", 4944
          "entry_mouse_source_time", 4952 ] do
        check
            "compiled capture configuration field offset"
            (int64 offset)
            (Marshal.OffsetOf<MacNative.CaptureConfig>(name).ToInt64())

    for ending in [ 0u; 1u; 2u ] do
        let terminal_config =
            match
                ConfigCompiler.compile
                    { ConfigSchema.defaults with
                        pan_hold = "MouseX1"
                        exit_on_mouse4 = true }
            with
            | Ok config -> config
            | Error error -> failwith error

        let release = event 4u 3u 0u 1.03
        let mutable release = release
        release.reserved <- 2u

        let records =
            [| event 8u 0u 0u 1.
               event 3u 3u 1u 1.01
               event 4u 3u 1u 1.02
               release
               event 9u ending 0u 1.03 |]

        for partition in [ 1; 2; 4; 64; 8192 ] do
            let struct (_, _, transitions, targets) =
                run_using terminal_config (Array.zeroCreate 133) 0u false records partition (fun (state: FlyState) ->
                    check "terminal reconciles physical mouse state" false (PlatformFlightKeyboard.key_is_down 131)
                    check "terminal skips pan hold side effect" true state.keyboard_pan_held
                    check "terminal restores only the requested camera" (ending = 1u) state.restore_camera_on_exit

                    check
                        "terminal leaves navigation in pan"
                        true
                        (match state.active_mouse_navigation with
                         | MousePan _ -> true
                         | _ -> false))

            check "terminal preserves two preceding navigation transitions" 2 transitions
            check "terminal avoids an exit pivot target lookup" 2 targets

    let pan_config =
        match
            ConfigCompiler.compile
                { ConfigSchema.defaults with
                    pan_hold = "MouseX1" }
        with
        | Ok config -> config
        | Error error -> failwith error

    let records =
        [| event 8u 0u 0u 1.
           event 3u 3u 1u 1.01
           event 4u 3u 1u 1.02
           event 4u 3u 0u 1.03
           event 9u 0u 0u 1.04 |]

    let struct (_, _, transitions, targets) =
        run_using pan_config (Array.zeroCreate 133) 0u false records 1 (fun (state: FlyState) ->
            check "ordinary pan release applies its hold action" false state.keyboard_pan_held

            check
                "ordinary pan release returns to pivot"
                true
                (match state.active_mouse_navigation with
                 | MousePivot _ -> true
                 | _ -> false))

    check "ordinary pan release exercises real navigation transitions" 3 transitions
    check "ordinary pan release resolves the next pivot target" 3 targets

    for expected, actual in [ 5u, 4u; 4u, 5u ] do
        let rejected =
            try
                MacNative.validate_bridge expected actual 160u 24u 4960u
                false
            with :? InvalidOperationException ->
                true

        check "mixed bridge contract is rejected" true rejected

    MacNative.validate_bridge 5u 5u 160u 24u 4960u
    check "native and managed protocol declares current ABI" 5u MacNative.BRIDGE_ABI

    let failed_input = InputAccumulator.create ()

    let failed_session =
        make_record<MacNavigationInput.Session>
            [ "id", box 1L
              "input", box failed_input
              "active", box true
              "worker", box true ]

    MacNative.api <- Some(make_record<MacNative.Api> [ ("raw_revoke", box (MacNative.Discard(ignore))) ])
    MacNavigationInput.current <- Some failed_session

    let rejected =
        try
            MacNavigationInput.handle_native (event 9u 99u 0u 1.) |> ignore
            false
        with :? InvalidOperationException as error ->
            error.Message.Contains "unknown End reason"

    check "unknown End is a protocol failure" true rejected
    check "unknown End revokes transport" false failed_session.active

    check
        "unknown End retains failure reason"
        true
        (match InputAccumulator.exit_reason failed_input with
         | Some(SessionFailure message) -> message.Contains "99"
         | _ -> false)

    MacNavigationInput.current <- None
    PlatformFlightKeyboard.stop ()

    let state = state_with config
    state.projection <- ViewProjectionKind.Parallel
    state.parallel_width <- 100.
    FlightCamera.record_parallel_magnification 2. state
    check "wheel establishes pending width before keyboard" 50. state.parallel_width
    let keyboard_factor = FlightCamera.parallel_magnification_factor state 0.2 0.02

    let expected_factor =
        FlightCamera.parallel_zoom_factor 50. 0.2 config.movement.parallel_projection.zoom_speed_multiplier 0.02

    check "keyboard zoom uses pending wheel width" expected_factor keyboard_factor
    check "keyboard magnification affects pending width once" (50. / keyboard_factor) state.parallel_width
    FlightCamera.record_parallel_magnification 0.5 state
    check "direct parallel zoom shares pending projection" (100. / keyboard_factor) state.parallel_width

match fsi.CommandLineArgs |> Array.skip 1 with
| [| "--managed-smoke" |] ->
    managed_smoke ()
    printfn "%d managed handler, replay and projection checks passed. Native traces were not executed." checks
| [| path |] ->
    read_traces path
    printfn "%d production native/managed protocol checks passed." checks
| _ -> failwith "Expected a production C trace file, or --managed-smoke."

MacNative.api <- None
