module RhinosCanFly.PlatformFlightKeyboard

open System
open System.Diagnostics

let down = Array.zeroCreate<bool> 133
let observed = Array.zeroCreate<bool> 133
let configured = Array.zeroCreate<bool> 133
let mutable actions: FlightBindingActions.State option = None
let mutable bindings: FlightBindings option = None
let mutable input: InputAccumulator.State option = None
let mutable key_revision = 0L
let mutable accept_new_keys = false
let mutable timestamp_offset = 0L
let mutable entry_releases = 0u
let mutable cancel_observed = false
let mutable exit_observed = false

let key_state (keys: bool array) (code: int) =
    match code with
    | 36 -> keys[36] || keys[52] || keys[76]
    | 133 -> keys[56] || keys[60]
    | 134 -> keys[59] || keys[62]
    | 135 -> keys[58] || keys[61]
    | 136 -> keys[55] || keys[54]
    | code when code >= 0 && code < keys.Length -> keys[code]
    | _ -> false

let key_is_down (code: int) = key_state down code

let binding_matches (state: bool array) (binding: KeyBinding) =
    let keys = binding.native_keys
    let mutable result = keys.Length > 0
    let mutable index = 0

    while result && index < keys.Length do
        result <- key_state state keys[index]
        index <- index + 1

    result

let binding_is_down (binding: KeyBinding) = binding_matches down binding

let collect_actions () =
    match actions with
    | Some actions -> FlightBindingActions.collect binding_is_down actions
    | None -> InputAccumulator.KeyboardAction.None

let configure (config: FlyConfig) (accumulator: InputAccumulator.State) (offset: int64) (entry_buttons: uint32) =
    FlightBindingActions.validate config
    Array.Clear down
    Array.Clear observed
    Array.Clear configured
    timestamp_offset <- offset
    entry_releases <- entry_buttons
    let keys = config.bindings

    let add (binding: KeyBinding) =
        for code in binding.native_keys do
            match code with
            | 36 ->
                configured[36] <- true
                configured[52] <- true
                configured[76] <- true
            | 133 ->
                configured[56] <- true
                configured[60] <- true
            | 134 ->
                configured[59] <- true
                configured[62] <- true
            | 135 ->
                configured[58] <- true
                configured[61] <- true
            | 136 ->
                configured[55] <- true
                configured[54] <- true
            | code -> configured[code] <- true

    FlightBindingActions.iter_bindings (fun (_name: string) (binding: KeyBinding) -> add binding) config

    for code = 0 to down.Length - 1 do
        if configured[code] || code >= 128 then
            let held = PlatformBindings.physical_key_down code
            down[code] <- held
            observed[code] <- held

    bindings <- Some keys
    cancel_observed <- binding_matches observed keys.cancel_flight_and_restore
    exit_observed <- binding_matches observed keys.exit_key
    input <- Some accumulator
    actions <- Some(FlightBindingActions.create config binding_is_down)
    accept_new_keys <- true

let configured_key (code: int) =
    accept_new_keys && code >= 0 && code < configured.Length && configured[code]

let modifier_is_down (code: int) (flags: uint64) =
    // Device-side masks from Apple's IOLLEvent.h retain left/right identity.
    let mask =
        match code with
        | 59 -> 0x00000001UL
        | 56 -> 0x00000002UL
        | 60 -> 0x00000004UL
        | 55 -> 0x00000008UL
        | 54 -> 0x00000010UL
        | 58 -> 0x00000020UL
        | 61 -> 0x00000040UL
        | 57 -> 0x00000080UL
        | 62 -> 0x00002000UL
        | 63 -> 0x00800000UL
        | _ -> 0UL

    flags &&& mask <> 0UL

let observe (timestamp: int64) (code: int) (held: bool) =
    if code >= 0 && code < observed.Length && observed[code] <> held then
        observed[code] <- held
        key_revision <- key_revision + 1L

        match input with
        | Some input -> InputAccumulator.add_keyboard_transition timestamp code held input
        | None -> ()

let apply_keyboard_transition (key: int) (held: bool) =
    if key >= 0 && key < down.Length then
        down[key] <- held

    collect_actions ()

let apply_raw_mouse_button_transition (transition: RawMouseButtonTransition) =
    let event = int transition.event

    if event >= 1 && event <= 10 then
        let code = 128 + (event - 1) / 2
        down[code] <- event % 2 = 1

    collect_actions ()

let terminal_timeline = InputAccumulator.timeline_buffer ()

let resolve_pending_exit
    (lifetime: FlightLifetime)
    (exit_buttons: MouseExitConfig)
    (accumulator: InputAccumulator.State)
    =
    match input with
    | Some input when obj.ReferenceEquals(input, accumulator) ->
        let struct (count, overflowed) =
            InputAccumulator.drain_timeline terminal_timeline input

        let mutable reason = InputAccumulator.exit_reason input
        let mutable index = 0

        while index < count && Option.isNone reason do
            let event = terminal_timeline[index]

            let actions =
                match event.kind with
                | InputAccumulator.TimelineEventKind.KeyboardTransition ->
                    apply_keyboard_transition event.key event.key_down
                | InputAccumulator.TimelineEventKind.RawMouseButton -> apply_raw_mouse_button_transition event.button
                | _ -> InputAccumulator.KeyboardAction.None

            let button =
                if event.kind = InputAccumulator.TimelineEventKind.RawMouseButton then
                    event.button.event
                else
                    RawMouseButtonEvent.None

            reason <- InputAccumulator.event_exit lifetime exit_buttons actions button
            index <- index + 1

        match reason with
        | Some _ -> reason
        | None when overflowed -> Some(SessionFailure "The input timeline overflowed before exit.")
        | None -> None
    | _ -> None

let binding_exit (lifetime: FlightLifetime) (exit_buttons: MouseExitConfig) (accumulator: InputAccumulator.State) =
    match bindings with
    | Some keys ->
        let cancel = binding_matches observed keys.cancel_flight_and_restore
        let exit = binding_matches observed keys.exit_key
        let pressed = (cancel && not cancel_observed) || (exit && not exit_observed)
        cancel_observed <- cancel
        exit_observed <- exit

        if pressed then
            resolve_pending_exit lifetime exit_buttons accumulator
        else
            None
    | None -> None

let observe_mouse (timestamp: int64) (transition: RawMouseButtonTransition) =
    let event = int transition.event

    if event >= 1 && event <= 10 then
        let code = 128 + (event - 1) / 2
        let held = event % 2 = 1
        let entry_release = not held && entry_releases &&& (1u <<< (code - 128)) <> 0u

        entry_releases <- entry_releases &&& ~~~(1u <<< (code - 128))

        if observed[code] <> held then
            observed[code] <- held

            match input with
            | Some input when entry_release -> InputAccumulator.add_keyboard_transition timestamp code false input
            | Some input ->
                InputAccumulator.add_boundary_event
                    { kind = InputAccumulator.TimelineEventKind.RawMouseButton
                      timestamp = timestamp
                      dx = 0L
                      dy = 0L
                      wheel = 0L
                      button = transition
                      key = 0
                      key_down = false }
                    input
            | None -> ()

let reconcile_physical_keys () =
    if (Platform.Mac.MacNative.load ()).keyboard_boundary.Invoke() = 0. then
        for code = 0 to 127 do
            if observed[code] && not (PlatformBindings.physical_key_down code) then
                observe (Stopwatch.GetTimestamp()) code false

        for code = 128 to observed.Length - 1 do
            if observed[code] && not (PlatformBindings.physical_key_down code) then
                observe_mouse
                    (Stopwatch.GetTimestamp())
                    { event = enum<RawMouseButtonEvent> (2 * (code - 128) + 2)
                      modifiers = MouseModifiers.none }

        match bindings with
        | Some keys ->
            cancel_observed <- binding_matches observed keys.cancel_flight_and_restore
            exit_observed <- binding_matches observed keys.exit_key
        | None -> ()

let consume_escape_exit (_lifetime: FlightLifetime) (_exit_buttons: MouseExitConfig) (_input: InputAccumulator.State) =
    ()

let revision () = key_revision

let movement_boundary () =
    let pending = (Platform.Mac.MacNative.load ()).keyboard_boundary.Invoke()
    let now = Stopwatch.GetTimestamp()

    let boundary =
        if pending > 0. then
            min now (timestamp_offset + int64 (pending * float Stopwatch.Frequency))
        else
            now

    struct (now, boundary)

let allow_passthrough () = accept_new_keys <- false

let stop () =
    accept_new_keys <- false
    entry_releases <- 0u
    actions <- None
    bindings <- None
    input <- None
    Array.Clear down
    Array.Clear observed
