// Use the compiled Mac plugin and matching RhinoCommon/Eto references, as with check-mac-keyboard.fsx.
open System
open System.Diagnostics
open System.IO
open System.Runtime.InteropServices
open Rhino.Geometry
open RhinosCanFly

let mutable checks = 0

let check (name: string) (expected: 'T) (actual: 'T) =
    checks <- checks + 1

    if actual <> expected then
        failwithf "%s: expected %A, got %A" name expected actual

let near (name: string) (expected: float) (actual: float) =
    checks <- checks + 1

    if abs (actual - expected) > 1e-10 then
        failwithf "%s: expected %g, got %g" name expected actual

let config =
    match ConfigCompiler.compile ConfigSchema.defaults with
    | Ok config -> config
    | Error error -> failwith error

check "managed event ABI" 160 (Marshal.SizeOf<Platform.Mac.MacNative.InputEvent>())
check "managed capture ABI" 4960 (Marshal.SizeOf<Platform.Mac.MacNative.CaptureConfig>())

check "held key at frozen frontier" false (FlightLoop.movement_is_actionable true true 100L 100L)
check "held key at advanced frontier" true (FlightLoop.movement_is_actionable true true 100L 120L)
check "idle key at advanced frontier" false (FlightLoop.movement_is_actionable true false 100L 120L)
check "Windows held key keeps its clock" true (FlightLoop.movement_is_actionable false true 100L 100L)

do
    let mutable signals = 0
    let wake = MacInputWake.State((fun () -> signals <- signals + 1), ignore)
    wake.Request false
    check "first movement wake is signalled" 1 signals
    wake.Dispatch()
    check "wake begins replay" true (wake.TryBegin())
    wake.Complete(FlightLoop.movement_is_actionable true true 100L 100L, false)

    for _boundary = 1 to 100 do
        wake.Boundary()

    check "frozen frontier does not repeatedly wake main" 1 signals
    wake.Request false
    check "new native frontier wakes main promptly" 2 signals

let ticks (milliseconds: int) =
    int64 milliseconds * Stopwatch.Frequency / 1000L

let pauses = ResizeArray<struct (int64 * int64)>()

let integrate (frames: int list) =
    let edges = [ 10, true; 30, false ]
    let mutable previous = 0L
    let mutable edge_index = 0
    let mutable held = false
    let mutable distance = 0.

    let advance (frame: int64) (boundary: int64) =
        let struct (finish, seconds) =
            FlightLoop.movement_interval previous frame boundary pauses

        if held then
            distance <- distance + seconds

        previous <- finish

    for frame in frames do
        while edge_index < edges.Length && fst edges[edge_index] <= frame do
            let time, down = edges[edge_index]
            advance (ticks frame) (ticks time)
            held <- down
            edge_index <- edge_index + 1

        advance (ticks frame) (ticks frame)

    distance

for frames in
    [ [ 100 ]
      [ 250 ]
      [ 16; 33; 50; 66; 83; 100 ]
      [ 33; 66; 100 ]
      [ 10; 30; 100 ]
      [ 5; 15; 25; 35; 100 ] ] do
    near "captured 20ms press survives redraw cadence" 0.02 (integrate frames)

let struct (_, delayed_seconds) =
    FlightLoop.movement_interval 0L (ticks 250) (ticks 250) pauses

near "ordinary 250ms interval is preserved" 0.25 delayed_seconds
pauses.Add(struct (ticks 10, ticks 20))

let struct (_, paused_seconds) =
    FlightLoop.movement_interval 0L (ticks 100) (ticks 30) pauses

near "explicit target work is excluded" 0.02 paused_seconds
pauses.Clear()

let input = InputAccumulator.create ()
InputAccumulator.add_timed_mouse (ticks 10) 0L -20L input
InputAccumulator.add_timed_mouse (ticks 20) 0L 20L input
let buffer = InputAccumulator.timeline_buffer ()
let struct (count, overflow) = InputAccumulator.drain_timeline buffer input
check "opposite motion remains two samples" 2 count
check "motion sample capacity" false overflow

do
    let backlog = InputAccumulator.create_with_capacity 8192

    for index = 0 to 8191 do
        InputAccumulator.add_timed_mouse (int64 index) 1L 0L backlog

    let samples = InputAccumulator.timeline_buffer_for backlog
    let struct (count, overflow) = InputAccumulator.drain_timeline samples backlog
    check "full native replay batch fits managed input" 8192 count
    check "full replay batch keeps every sample" false overflow
    check "full replay batch keeps last timestamp" 8191L samples[count - 1].timestamp

let camera (pitch: float) : CameraState =
    let direction = Vector3d(0., cos pitch, sin pitch)

    { position = Point3d.Origin
      target = Point3d(direction.X, direction.Y, direction.Z)
      direction = direction
      up = Vector3d(0., -sin pitch, cos pitch) }

let sensitivity = MouseRadiansPerCount(Math.PI / 180.)
let mutable pitch = 80. * Math.PI / 180.

for index = 0 to count - 1 do
    let event = buffer[index]

    let delta =
        Movement.clamped_mouse_angle_deltas config.mouse sensitivity 1. event.dx event.dy (camera pitch)

    pitch <- pitch + delta.pitch_delta

near "sequential clamp keeps the return motion" 69. (pitch * 180. / Math.PI)

for mode in [ KeyActivationMode.Hold; KeyActivationMode.Toggle ] do
    let settings =
        { ConfigSchema.defaults with
            boost_mode = mode
            slow_mode = mode }

    let config =
        match ConfigCompiler.compile settings with
        | Ok config -> config
        | Error error -> failwith error

    let input = InputAccumulator.create ()
    PlatformFlightKeyboard.configure config input 0L 0u false (fun (_code: int) -> false)
    check "entry Shift is inactive" false (PlatformFlightKeyboard.binding_is_down config.bindings.boost)
    check "entry Option is inactive" false (PlatformFlightKeyboard.binding_is_down config.bindings.slow)
    PlatformFlightKeyboard.observe (ticks 10) 56 true
    let actions = PlatformFlightKeyboard.apply_keyboard_transition 56 true
    check "fresh Shift becomes eligible" true (PlatformFlightKeyboard.binding_is_down config.bindings.boost)

    check
        "fresh Shift toggle follows configured mode"
        (mode = KeyActivationMode.Toggle)
        (int actions &&& int InputAccumulator.KeyboardAction.BoostToggle <> 0)

    PlatformFlightKeyboard.stop ()

do
    let input = InputAccumulator.create ()
    PlatformFlightKeyboard.configure config input 0L 0u false ((=) 13)

    let session: MacNavigationInput.Session =
        { id = 9L
          input = input
          window = 0n
          timestamp_offset = 0L
          started_at = 0.
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
          begun = true
          wheel_remainder = 0. }

    MacNavigationInput.current <- Some session
    let mutable release = Unchecked.defaultof<Platform.Mac.MacNative.InputEvent>
    release.kind <- 3u
    release.code <- 13u
    release.session <- 8UL
    check "old session Up is rejected" 0u (MacNavigationInput.handle_native release)
    check "old session Up leaves fresh press held" true (PlatformFlightKeyboard.key_is_down 13)
    release.session <- 9UL
    check "current session Up is accepted" 1u (MacNavigationInput.handle_native release)
    let struct (count, overflow) = InputAccumulator.drain_timeline buffer input
    check "only current release entered replay" 1 count
    check "session release capacity" false overflow

    PlatformFlightKeyboard.apply_keyboard_transition buffer[0].key buffer[0].key_down
    |> ignore

    check "current release ends movement" false (PlatformFlightKeyboard.key_is_down 13)

    let mutable ending = release
    ending.kind <- 9u
    ending.code <- 1u
    check "normal exit enters ordered replay" 1u (MacNavigationInput.handle_native ending)
    check "normal exit preserves camera permission until replay" None (InputAccumulator.exit_reason input)
    let struct (count, _) = InputAccumulator.drain_timeline buffer input
    check "ordered exit record count" 1 count
    check "ordered exit retains restore choice" InputAccumulator.TimelineEventKind.ExitRestoreCamera buffer[0].kind

    MacNavigationInput.current <- None
    PlatformFlightKeyboard.stop ()

    let mutable context = Unchecked.defaultof<Platform.Mac.MacNative.InputEvent>
    context.code <- 7u
    context.modifiers <- 1UL <<< 17
    let capture = MacNavigationInput.capture_config session false (Some context)
    check "arbitrary entry key is excluded" 1uy capture.quarantine[7]
    check "entry Shift is excluded on either side" 1uy capture.quarantine[60]
    check "fresh W remains eligible during shortcut" 0uy capture.quarantine[13]
    let fallback = MacNavigationInput.capture_config session false None
    check "unknown command gates preheld configured W" 1uy fallback.quarantine[13]
    let mouse = MacNavigationInput.capture_config session true None
    check "mouse entry keeps intentionally held W" 0uy mouse.quarantine[13]

    let memory =
        Marshal.AllocHGlobal(Marshal.SizeOf<Platform.Mac.MacNative.CaptureConfig>())

    try
        Marshal.StructureToPtr(capture, memory, false)
        check "native receives session" 9L (Marshal.ReadInt64 memory)
        check "native receives shortcut gate" 1uy (Marshal.ReadByte(memory, 294 + 7))
        check "native receives terminal chord size" (int capture.terminal[0].count) (Marshal.ReadInt32(memory, 560))
        let roundtrip = Marshal.PtrToStructure<Platform.Mac.MacNative.CaptureConfig> memory
        check "nested native chord arrays survive marshalling" capture.terminal[0].keys roundtrip.terminal[0].keys
    finally
        Marshal.DestroyStructure<Platform.Mac.MacNative.CaptureConfig> memory
        Marshal.FreeHGlobal memory

    match fsi.CommandLineArgs |> Array.skip 1 with
    | [| "--binding-fixture"; path |] ->
        let settings =
            [ ConfigSchema.defaults
              { ConfigSchema.defaults with
                  exit_key = "Shift+E"
                  cancel_flight_and_restore = "Command+T" }
              { ConfigSchema.defaults with
                  exit_key = "Enter"
                  cancel_flight_and_restore = "Alt+MouseX1" } ]

        use stream = File.Create path
        use writer = new BinaryWriter(stream)
        writer.Write(uint32 (settings.Length * 256 * 2))
        let random = Random 173

        for settings in settings do
            let config =
                match ConfigCompiler.compile settings with
                | Ok config -> config
                | Error error -> failwith error

            let capture =
                MacNavigationInput.capture_config { session with config = config } true None

            let bindings =
                [| config.bindings.exit_key; config.bindings.cancel_flight_and_restore |]

            for _sample = 1 to 256 do
                let held = Array.init 133 (fun (_code: int) -> random.Next(2) = 1)

                for index = 0 to 1 do
                    writer.Write capture.terminal[index].count

                    for code in capture.terminal[index].keys do
                        writer.Write code

                    for down in held do
                        writer.Write(byte (if down then 1 else 0))

                    writer.Write(
                        byte (
                            if PlatformFlightKeyboard.binding_matches held bindings[index] then
                                1
                            else
                                0
                        )
                    )
    | [||] -> ()
    | _ -> failwith "Expected --binding-fixture path, or no arguments."

do
    let rotated =
        Movement.rotate_vector (Vector3d(0., 0., 10.)) (Math.PI / 2.) Vector3d.XAxis

    near "rotation normalizes its axis" 1. rotated.Y
    near "rotation keeps vector length" 1. rotated.Length

    check
        "invalid rotation axis leaves vector alone"
        Vector3d.XAxis
        (Movement.rotate_vector Vector3d.Zero 1. Vector3d.XAxis)

    let pivot = PivotOrbit.create (Point3d(0., 5., 0.)) (camera 0.) 0.01 0.01
    let moved = PivotOrbit.apply_delta 10L -5L pivot
    check "pivot replay is valid without Rhino" true (CameraState.valid moved)

    let replay (frames: int list) =
        let events =
            [ 10, 3, 13, 1L, 0L; 20, 1, 0, 20L, 0L; 30, 1, 0, -5L, 2L; 80, 3, 13, 0L, 0L ]

        let mutable event_index = 0
        let mutable previous = 0L
        let mutable current = camera 0.
        let mutable moving = false

        let advance (frame: int64) (boundary: int64) =
            let struct (finish, seconds) =
                FlightLoop.movement_interval previous frame boundary pauses

            let movement: FlightMovementInput =
                { forward = moving
                  backward = false
                  left = false
                  right = false
                  up = false
                  down = false
                  key_pivot_left = false
                  key_pivot_right = false
                  move_speed = 10. }

            current <- (Movement.step config.movement 1. ValueNone movement Point3d.Origin seconds current).camera
            previous <- finish

        for frame in frames do
            while event_index < events.Length
                  && (let time, _, _, _, _ = events[event_index] in time <= frame) do
                let time, kind, code, dx, dy = events[event_index]
                advance (ticks frame) (ticks time)

                if kind = 1 then
                    current <- Movement.mouse_look config.mouse sensitivity dx dy current
                else
                    moving <- code = 13 && dx = 1L

                event_index <- event_index + 1

            advance (ticks frame) (ticks frame)

        current

    let reference = replay [ 100 ]

    let random = Random 73

    let schedules =
        [ [ 250 ]
          [ 16; 33; 50; 66; 83; 100 ]
          [ 33; 66; 100 ]
          [ 10; 20; 30; 80; 100 ]
          [ 5; 15; 25; 35; 50; 90; 100 ]
          for _sample = 1 to 64 do
              [ for _frame = 1 to 10 do
                    random.Next(1, 100)
                100 ]
              |> List.distinct
              |> List.sort ]

    for frames in schedules do
        let actual = replay frames
        near "moving and turning position X" reference.position.X actual.position.X
        near "moving and turning position Y" reference.position.Y actual.position.Y
        near "moving and turning direction Z" reference.direction.Z actual.direction.Z

printfn "%d compiled Mac replay checks passed." checks
