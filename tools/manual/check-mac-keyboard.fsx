// Run against the built Mac plugin copied to a .dll, with RhinoCommon and Eto passed as dotnet fsi --reference.
open RhinosCanFly

let mutable checks = 0

let check (name: string) (expected: 'T) (actual: 'T) =
    checks <- checks + 1

    if actual <> expected then
        failwithf "%s: expected %A, got %A" name expected actual

let compile (settings: FlyConfigFile) =
    match ConfigCompiler.compile settings with
    | Ok config -> config
    | Error error -> failwith error

let contains (action: InputAccumulator.KeyboardAction) (actions: InputAccumulator.KeyboardAction) =
    int actions &&& int action <> 0

let replay (config: FlyConfig) (initial: int list) (pulses: bool) (edges: (int * bool * bool) list) =
    let input = InputAccumulator.create ()
    let buffer = InputAccumulator.timeline_buffer ()
    let emitted = ResizeArray<InputAccumulator.KeyboardAction>()
    PlatformFlightKeyboard.configure config input 0L 0u true (fun (code: int) -> List.contains code initial)

    let drain () =
        let struct (count, overflowed) = InputAccumulator.drain_timeline buffer input
        check "timeline capacity" false overflowed

        for index = 0 to count - 1 do
            let edge = buffer[index]

            let actions =
                if edge.kind = InputAccumulator.TimelineEventKind.RawMouseButton then
                    PlatformFlightKeyboard.apply_raw_mouse_button_transition edge.button
                else
                    PlatformFlightKeyboard.apply_keyboard_transition edge.key edge.key_down

            if actions <> InputAccumulator.KeyboardAction.None then
                emitted.Add actions

    for code, held, repeated in edges do
        PlatformFlightKeyboard.prepare_transition code held repeated

        if code < 128 then
            PlatformFlightKeyboard.observe 100L code held
        else
            PlatformFlightKeyboard.observe_mouse
                100L
                { event = enum<RawMouseButtonEvent> (2 * (code - 128) + (if held then 1 else 2))
                  modifiers = MouseModifiers.none }

        if pulses then
            drain ()

    drain ()
    PlatformFlightKeyboard.stop ()
    List.ofSeq emitted

let projection = InputAccumulator.KeyboardAction.ProjectionToggle

let config =
    compile
        { ConfigSchema.defaults with
            toggle_projection = "Shift+A" }

for shift in [ 56; 60 ] do
    for pulses in [ false; true ] do
        let start = [ shift, true, false; 0, true, false ]
        let actions = replay config [ shift; 0 ] pulses start
        check "sampled chord Down" 1 (actions |> List.filter (contains projection) |> List.length)

        let partial = replay config [ shift ] pulses [ 0, true, false; shift, true, false ]
        check "partially sampled chord" 1 (partial |> List.filter (contains projection) |> List.length)

        let next_press = start @ [ 0, false, false; shift, false, false ] @ start
        let actions = replay config [ shift; 0 ] pulses next_press
        check "next chord press" 2 (actions |> List.filter (contains projection) |> List.length)

        let preheld =
            replay config [ shift; 0 ] pulses [ 0, true, true; 0, false, false; 0, true, false ]

        check "preheld repeat then fresh press" 1 (preheld |> List.filter (contains projection) |> List.length)

let hold =
    compile
        { ConfigSchema.defaults with
            pan_hold = "Shift+A" }

for initial in [ []; [ 56 ]; [ 56; 0 ] ] do
    for edges in [ [ 56, false, false; 0, false, false ]; [ 0, false, false; 56, false, false ] ] do
        let actions = replay hold initial false edges
        check "sampled chord release scheduling" actions (replay hold initial true edges)

        if initial = [ 56; 0 ] then
            check
                "preheld chord releases once"
                1
                (actions
                 |> List.filter (contains InputAccumulator.KeyboardAction.PanHoldEnded)
                 |> List.length)

for edges in
    [ [ 56, true, false; 60, true, false; 0, true, false ]
      [ 0, true, false; 60, false, false; 56, false, false ] ] do
    check
        "either-side modifier scheduling"
        (replay config [ 56; 60; 0 ] false edges)
        (replay config [ 56; 60; 0 ] true edges)

for setting, key, action in
    [ "Escape", 53, InputAccumulator.KeyboardAction.Exit
      "T", 17, InputAccumulator.KeyboardAction.CancelAndRestore ] do
    let config = compile ConfigSchema.defaults
    let actions = replay config [ key ] false (List.singleton (key, true, false))
    check setting 1 (actions |> List.filter (contains action) |> List.length)
    check "preheld terminal repeat" [] (replay config [ key ] true (List.singleton (key, true, true)))

    let input = InputAccumulator.create ()
    PlatformFlightKeyboard.configure config input 0L 0u true ((=) key)
    PlatformFlightKeyboard.prepare_transition key true false
    PlatformFlightKeyboard.observe 100L key true

    let reason =
        PlatformFlightKeyboard.binding_exit FlightLifetime.UntilExit config.mouse.exit_buttons input

    check "startup terminal action" true (Option.isSome reason)
    PlatformFlightKeyboard.stop ()

let mouse_config =
    compile
        { ConfigSchema.defaults with
            toggle_projection = "MouseX1" }

for pulses in [ false; true ] do
    let actions =
        replay mouse_config [ 131 ] pulses [ 131, true, false; 131, false, false; 131, true, false ]

    check "sampled mouse binding" 2 (actions |> List.filter (contains projection) |> List.length)

let input = InputAccumulator.create ()
PlatformFlightKeyboard.configure mouse_config input 0L 0u true ((=) 131)
PlatformFlightKeyboard.prepare_transition 131 true false
check "capture leaves camera-side state alone" true (PlatformFlightKeyboard.key_is_down 131)
PlatformFlightKeyboard.stop ()

printfn "%d compiled Mac keyboard checks passed." checks
