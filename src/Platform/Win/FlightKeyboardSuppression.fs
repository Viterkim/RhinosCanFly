module RhinosCanFly.PlatformFlightKeyboard

open System
open System.Collections.Generic
open System.Diagnostics
open System.Threading
open Rhino
open RhinosCanFly
open RhinosCanFly.Platform.Win

type ConfiguredKeys =
    { exact: HashSet<int>
      mutable either_shift: bool
      mutable either_control: bool
      mutable either_alt: bool }

type State =
    { transition_gate: obj
      configured: ConfiguredKeys
      passthrough_keys_down: HashSet<int>
      suppressed_keys_down: HashSet<int>
      key_is_down: bool array
      observed_key_is_down: bool array
      mouse_key_configured: bool array
      mutable bindings: FlightBindings option
      mutable boost_mode: KeyActivationMode
      mutable slow_mode: KeyActivationMode
      mutable retarget_all_views_binding: KeyBinding option
      mutable retarget_other_views_binding: KeyBinding option
      mutable pivot_toggle_down: bool
      mutable pan_toggle_down: bool
      mutable pivot_hold_down: bool
      mutable pan_hold_down: bool
      mutable boost_down: bool
      mutable slow_down: bool
      mutable speed_increase_down: bool
      mutable speed_decrease_down: bool
      mutable projection_toggle_down: bool
      mutable retarget_all_views_down: bool
      mutable retarget_other_views_down: bool
      mutable untilt_view_down: bool
      mutable exit_down: bool
      mutable cancel_and_restore_down: bool
      mutable input: InputAccumulator.State option
      mutable input_available: Action option
      mutable revision: int64
      mutable accept_new_keys: bool
      mutable active: bool }

let state =
    { transition_gate = obj ()
      configured =
        { exact = HashSet<int>()
          either_shift = false
          either_control = false
          either_alt = false }
      passthrough_keys_down = HashSet<int>()
      suppressed_keys_down = HashSet<int>()
      key_is_down = Array.zeroCreate 256
      observed_key_is_down = Array.zeroCreate 256
      mouse_key_configured = Array.zeroCreate 256
      bindings = None
      boost_mode = KeyActivationMode.Hold
      slow_mode = KeyActivationMode.Hold
      retarget_all_views_binding = None
      retarget_other_views_binding = None
      pivot_toggle_down = false
      pan_toggle_down = false
      pivot_hold_down = false
      pan_hold_down = false
      boost_down = false
      slow_down = false
      speed_increase_down = false
      speed_decrease_down = false
      projection_toggle_down = false
      retarget_all_views_down = false
      retarget_other_views_down = false
      untilt_view_down = false
      exit_down = false
      cancel_and_restore_down = false
      input = None
      input_available = None
      revision = 0L
      accept_new_keys = false
      active = false }

let mutable keyboard_hook: Win32Native.WindowsHook option = None

let is_plain_escape (binding: KeyBinding) =
    let keys = binding.virtual_keys

    if keys.Length = 1 then
        let (VirtualKey virtual_key) = keys[0]
        virtual_key = Win32Native.VK_ESCAPE
    else
        false

let try_request_plain_escape_exit () =
    if
        not (Volatile.Read(&state.active))
        || not (Volatile.Read(&state.accept_new_keys))
    then
        false
    else
        match state.bindings with
        | Some bindings ->
            if
                is_plain_escape bindings.cancel_flight_and_restore
                || is_plain_escape bindings.exit_key
            then
                match state.input with
                | Some input ->
                    // End publication here; earlier cancellation transitions still get consumed.
                    InputAccumulator.add_keyboard_transition (Stopwatch.GetTimestamp()) Win32Native.VK_ESCAPE true input
                    Volatile.Write(&state.accept_new_keys, false)
                    Volatile.Write(&input.escape_requested, true)
                    true
                | None -> false
            else
                false
        | None -> false

let escape_key_pressed =
    EventHandler(fun (_: obj) (_: EventArgs) ->
        Monitor.Enter state.transition_gate

        try
            let requested = try_request_plain_escape_exit ()

            if requested then
                match state.input_available with
                | Some available -> available.Invoke()
                | None -> ()
        finally
            Monitor.Exit state.transition_gate)

let mutable escape_handler_installed = false

let clear_configured () =
    state.configured.exact.Clear()
    state.configured.either_shift <- false
    state.configured.either_control <- false
    state.configured.either_alt <- false

let add_key (key: VirtualKey) =
    let (VirtualKey virtual_key) = key

    match virtual_key with
    | Win32Native.VK_SHIFT -> state.configured.either_shift <- true
    | Win32Native.VK_CONTROL -> state.configured.either_control <- true
    | Win32Native.VK_MENU -> state.configured.either_alt <- true
    | _ -> state.configured.exact.Add virtual_key |> ignore

let add_binding (binding: KeyBinding) =
    for key in binding.virtual_keys do
        add_key key

let add_optional_binding (binding: KeyBinding option) =
    match binding with
    | Some value -> add_binding value
    | None -> ()

let configured_key (physical_key: int) =
    state.configured.exact.Contains physical_key
    || (state.configured.either_shift
        && (physical_key = Win32Native.VK_LSHIFT || physical_key = Win32Native.VK_RSHIFT))
    || (state.configured.either_control
        && (physical_key = Win32Native.VK_LCONTROL || physical_key = Win32Native.VK_RCONTROL))
    || (state.configured.either_alt
        && (physical_key = Win32Native.VK_LMENU || physical_key = Win32Native.VK_RMENU))

let seed_held_key (physical_key: int) (down: bool) =
    if configured_key physical_key && down then
        if not (state.suppressed_keys_down.Contains physical_key) then
            state.passthrough_keys_down.Add physical_key |> ignore

        state.key_is_down[physical_key] <- true
        state.observed_key_is_down[physical_key] <- true

let virtual_key_down (virtual_key: int) =
    match virtual_key with
    | Win32Native.VK_LBUTTON
    | Win32Native.VK_RBUTTON
    | Win32Native.VK_MBUTTON
    | Win32Native.VK_XBUTTON1
    | Win32Native.VK_XBUTTON2 -> Volatile.Read(&state.key_is_down[virtual_key])
    | Win32Native.VK_SHIFT ->
        Volatile.Read(&state.key_is_down[Win32Native.VK_LSHIFT])
        || Volatile.Read(&state.key_is_down[Win32Native.VK_RSHIFT])
    | Win32Native.VK_CONTROL ->
        Volatile.Read(&state.key_is_down[Win32Native.VK_LCONTROL])
        || Volatile.Read(&state.key_is_down[Win32Native.VK_RCONTROL])
    | Win32Native.VK_MENU ->
        Volatile.Read(&state.key_is_down[Win32Native.VK_LMENU])
        || Volatile.Read(&state.key_is_down[Win32Native.VK_RMENU])
    | _ -> Volatile.Read(&state.key_is_down[virtual_key])

let binding_is_down (binding: KeyBinding) =
    if not (Volatile.Read(&state.active)) then
        PlatformBindings.is_down binding
    else
        let keys = binding.virtual_keys
        let mutable index = 0
        let mutable down = keys.Length > 0

        while down && index < keys.Length do
            let (VirtualKey virtual_key) = keys[index]
            down <- virtual_key_down virtual_key
            index <- index + 1

        down

let is_optional_binding_down (binding: KeyBinding option) =
    match binding with
    | Some value -> binding_is_down value
    | None -> false

let configure_with_snapshot
    (is_down: int -> bool)
    (config: FlyConfig)
    (input: InputAccumulator.State)
    (input_available: Action)
    =
    let bindings = config.bindings
    let retarget = config.behavior.retarget

    let enabled_binding (mode: RetargetMode) (binding: KeyBinding option) =
        if mode = RetargetMode.Off then None else binding

    let retarget_all_views_binding =
        enabled_binding retarget.keyboard_all_views bindings.retarget_all_views

    let retarget_other_views_binding =
        enabled_binding retarget.keyboard_other_views bindings.retarget_other_views

    System.Array.Clear(state.key_is_down, 0, state.key_is_down.Length)
    System.Array.Clear(state.observed_key_is_down, 0, state.observed_key_is_down.Length)

    clear_configured ()
    add_binding bindings.forward
    add_binding bindings.backward
    add_binding bindings.left
    add_binding bindings.right
    add_binding bindings.up
    add_binding bindings.down
    add_binding bindings.key_pivot_left
    add_binding bindings.key_pivot_right
    add_optional_binding bindings.mouse_navigation.pivot.toggle
    add_optional_binding bindings.mouse_navigation.pivot.hold
    add_optional_binding bindings.mouse_navigation.pan.toggle
    add_optional_binding bindings.mouse_navigation.pan.hold
    add_binding bindings.boost
    add_binding bindings.slow
    add_optional_binding bindings.speed_increase
    add_optional_binding bindings.speed_decrease
    add_optional_binding retarget_all_views_binding
    add_optional_binding retarget_other_views_binding
    add_optional_binding bindings.untilt_view
    add_binding bindings.exit_key
    add_binding bindings.cancel_flight_and_restore
    add_optional_binding bindings.toggle_projection

    state.mouse_key_configured[Win32Native.VK_LBUTTON] <- configured_key Win32Native.VK_LBUTTON
    state.mouse_key_configured[Win32Native.VK_RBUTTON] <- configured_key Win32Native.VK_RBUTTON
    state.mouse_key_configured[Win32Native.VK_MBUTTON] <- configured_key Win32Native.VK_MBUTTON
    state.mouse_key_configured[Win32Native.VK_XBUTTON1] <- configured_key Win32Native.VK_XBUTTON1
    state.mouse_key_configured[Win32Native.VK_XBUTTON2] <- configured_key Win32Native.VK_XBUTTON2

    state.passthrough_keys_down.Clear()

    for physical_key in state.configured.exact do
        seed_held_key physical_key (is_down physical_key)

    if state.configured.either_shift then
        seed_held_key Win32Native.VK_LSHIFT (is_down Win32Native.VK_LSHIFT)
        seed_held_key Win32Native.VK_RSHIFT (is_down Win32Native.VK_RSHIFT)

    if state.configured.either_control then
        seed_held_key Win32Native.VK_LCONTROL (is_down Win32Native.VK_LCONTROL)
        seed_held_key Win32Native.VK_RCONTROL (is_down Win32Native.VK_RCONTROL)

    if state.configured.either_alt then
        seed_held_key Win32Native.VK_LMENU (is_down Win32Native.VK_LMENU)
        seed_held_key Win32Native.VK_RMENU (is_down Win32Native.VK_RMENU)

    state.bindings <- Some bindings
    state.boost_mode <- config.movement.boost_mode
    state.slow_mode <- config.movement.slow_mode
    state.retarget_all_views_binding <- retarget_all_views_binding
    state.retarget_other_views_binding <- retarget_other_views_binding
    // Seed action edges from the same snapshot used by movement.
    Volatile.Write(&state.active, true)
    state.pivot_toggle_down <- is_optional_binding_down bindings.mouse_navigation.pivot.toggle
    state.pan_toggle_down <- is_optional_binding_down bindings.mouse_navigation.pan.toggle
    state.pivot_hold_down <- is_optional_binding_down bindings.mouse_navigation.pivot.hold
    state.pan_hold_down <- is_optional_binding_down bindings.mouse_navigation.pan.hold
    state.boost_down <- binding_is_down bindings.boost
    state.slow_down <- binding_is_down bindings.slow
    state.speed_increase_down <- is_optional_binding_down bindings.speed_increase
    state.speed_decrease_down <- is_optional_binding_down bindings.speed_decrease
    state.projection_toggle_down <- is_optional_binding_down bindings.toggle_projection
    state.retarget_all_views_down <- is_optional_binding_down retarget_all_views_binding
    state.retarget_other_views_down <- is_optional_binding_down retarget_other_views_binding
    state.untilt_view_down <- is_optional_binding_down bindings.untilt_view
    state.exit_down <- binding_is_down bindings.exit_key
    state.cancel_and_restore_down <- binding_is_down bindings.cancel_flight_and_restore
    state.input <- Some input
    state.input_available <- Some input_available
    Volatile.Write(&state.accept_new_keys, true)
    Volatile.Write(&state.active, true)

let configure_with_physical_snapshot
    (swapped: bool)
    (is_down: int -> bool)
    (config: FlyConfig)
    (input: InputAccumulator.State)
    (input_available: Action)
    =
    configure_with_snapshot
        (fun (key: int) -> is_down (Win32.physical_mouse_key swapped key))
        config
        input
        input_available

let stop_core () =
    Volatile.Write(&state.accept_new_keys, false)
    Volatile.Write(&state.active, false)
    state.input <- None
    state.input_available <- None
    state.bindings <- None
    state.boost_mode <- KeyActivationMode.Hold
    state.slow_mode <- KeyActivationMode.Hold
    state.retarget_all_views_binding <- None
    state.retarget_other_views_binding <- None
    state.pivot_toggle_down <- false
    state.pan_toggle_down <- false
    state.pivot_hold_down <- false
    state.pan_hold_down <- false
    state.boost_down <- false
    state.slow_down <- false
    state.speed_increase_down <- false
    state.speed_decrease_down <- false
    state.projection_toggle_down <- false
    state.retarget_all_views_down <- false
    state.retarget_other_views_down <- false
    state.untilt_view_down <- false
    state.exit_down <- false
    state.cancel_and_restore_down <- false
    clear_configured ()
    state.passthrough_keys_down.Clear()
    System.Array.Clear(state.key_is_down, 0, state.key_is_down.Length)
    System.Array.Clear(state.observed_key_is_down, 0, state.observed_key_is_down.Length)
    System.Array.Clear(state.mouse_key_configured, 0, state.mouse_key_configured.Length)

let stop () =
    Monitor.Enter state.transition_gate

    try
        stop_core ()
    finally
        Monitor.Exit state.transition_gate

let classify_fresh_key_down (physical_key: int) =
    if
        Volatile.Read(&state.active)
        && Volatile.Read(&state.accept_new_keys)
        && configured_key physical_key
    then
        state.suppressed_keys_down.Add physical_key |> ignore
        state.observed_key_is_down[physical_key] <- true
        true
    else
        false

let handle_event (event: Win32.KeyboardHookEvent) =
    let physical_key = event.physical_key

    if not (Volatile.Read(&state.active)) && state.suppressed_keys_down.Count = 0 then
        false
    elif event.released then
        let suppressed = state.suppressed_keys_down.Remove physical_key
        state.passthrough_keys_down.Remove physical_key |> ignore
        state.observed_key_is_down[physical_key] <- false
        suppressed
    elif state.suppressed_keys_down.Contains physical_key then
        if event.was_down then
            true
        else
            state.suppressed_keys_down.Remove physical_key |> ignore
            classify_fresh_key_down physical_key
    elif state.passthrough_keys_down.Contains physical_key then
        if event.was_down then
            true
        else
            state.passthrough_keys_down.Remove physical_key |> ignore
            classify_fresh_key_down physical_key
    else
        classify_fresh_key_down physical_key

let add_action (current: InputAccumulator.KeyboardAction) (added: InputAccumulator.KeyboardAction) =
    enum<InputAccumulator.KeyboardAction> (int current ||| int added)

let collect_actions () =
    match state.bindings with
    | None -> InputAccumulator.KeyboardAction.None
    | Some bindings ->
        let mutable actions = InputAccumulator.KeyboardAction.None

        let pivot_toggle = is_optional_binding_down bindings.mouse_navigation.pivot.toggle

        if pivot_toggle && not state.pivot_toggle_down then
            actions <- add_action actions InputAccumulator.KeyboardAction.PivotToggle

        state.pivot_toggle_down <- pivot_toggle

        let pan_toggle = is_optional_binding_down bindings.mouse_navigation.pan.toggle

        if pan_toggle && not state.pan_toggle_down then
            actions <- add_action actions InputAccumulator.KeyboardAction.PanToggle

        state.pan_toggle_down <- pan_toggle

        let pivot_hold = is_optional_binding_down bindings.mouse_navigation.pivot.hold

        if pivot_hold <> state.pivot_hold_down then
            actions <-
                add_action
                    actions
                    (if pivot_hold then
                         InputAccumulator.KeyboardAction.PivotHoldStarted
                     else
                         InputAccumulator.KeyboardAction.PivotHoldEnded)

        state.pivot_hold_down <- pivot_hold

        let pan_hold = is_optional_binding_down bindings.mouse_navigation.pan.hold

        if pan_hold <> state.pan_hold_down then
            actions <-
                add_action
                    actions
                    (if pan_hold then
                         InputAccumulator.KeyboardAction.PanHoldStarted
                     else
                         InputAccumulator.KeyboardAction.PanHoldEnded)

        state.pan_hold_down <- pan_hold

        let boost = binding_is_down bindings.boost

        if boost && not state.boost_down && state.boost_mode = KeyActivationMode.Toggle then
            actions <- add_action actions InputAccumulator.KeyboardAction.BoostToggle

        state.boost_down <- boost

        let slow = binding_is_down bindings.slow

        if slow && not state.slow_down && state.slow_mode = KeyActivationMode.Toggle then
            actions <- add_action actions InputAccumulator.KeyboardAction.SlowToggle

        state.slow_down <- slow

        let speed_increase = is_optional_binding_down bindings.speed_increase

        if speed_increase && not state.speed_increase_down then
            actions <- add_action actions InputAccumulator.KeyboardAction.SpeedIncrease

        state.speed_increase_down <- speed_increase

        let speed_decrease = is_optional_binding_down bindings.speed_decrease

        if speed_decrease && not state.speed_decrease_down then
            actions <- add_action actions InputAccumulator.KeyboardAction.SpeedDecrease

        state.speed_decrease_down <- speed_decrease

        let projection_toggle = is_optional_binding_down bindings.toggle_projection

        if projection_toggle && not state.projection_toggle_down then
            actions <- add_action actions InputAccumulator.KeyboardAction.ProjectionToggle

        state.projection_toggle_down <- projection_toggle

        let retarget_all = is_optional_binding_down state.retarget_all_views_binding

        if retarget_all && not state.retarget_all_views_down then
            actions <- add_action actions InputAccumulator.KeyboardAction.RetargetAllViews

        state.retarget_all_views_down <- retarget_all

        let retarget_other = is_optional_binding_down state.retarget_other_views_binding

        if retarget_other && not state.retarget_other_views_down then
            actions <- add_action actions InputAccumulator.KeyboardAction.RetargetOtherViews

        state.retarget_other_views_down <- retarget_other

        let untilt_view = is_optional_binding_down bindings.untilt_view

        if untilt_view && not state.untilt_view_down then
            actions <- add_action actions InputAccumulator.KeyboardAction.UntiltView

        state.untilt_view_down <- untilt_view

        let exit = binding_is_down bindings.exit_key

        if exit && not state.exit_down then
            actions <- add_action actions InputAccumulator.KeyboardAction.Exit

        state.exit_down <- exit

        let cancel_and_restore = binding_is_down bindings.cancel_flight_and_restore

        if cancel_and_restore && not state.cancel_and_restore_down then
            actions <- add_action actions InputAccumulator.KeyboardAction.CancelAndRestore

        state.cancel_and_restore_down <- cancel_and_restore
        actions

let admit_mouse_bindings (buttons: int) =
    Monitor.Enter state.transition_gate

    try
        for key in Win32Native.VK_LBUTTON .. Win32Native.VK_XBUTTON2 do
            if key <> Win32Native.VK_CANCEL && state.mouse_key_configured[key] then
                let down = buttons &&& (1 <<< key) <> 0
                state.key_is_down[key] <- down
                state.observed_key_is_down[key] <- down

                if not down then
                    state.passthrough_keys_down.Remove key |> ignore

        collect_actions () |> ignore
    finally
        Monitor.Exit state.transition_gate

let release_stale_key (physical_key: int) =
    if
        physical_key > Win32Native.VK_XBUTTON2
        && Volatile.Read(&state.observed_key_is_down[physical_key])
        && Win32Native.GetAsyncKeyState physical_key >= 0s
    then
        state.observed_key_is_down[physical_key] <- false
        state.passthrough_keys_down.Remove physical_key |> ignore

        match state.input with
        | Some input -> InputAccumulator.add_keyboard_transition (Stopwatch.GetTimestamp()) physical_key false input
        | None -> ()

        true
    else
        false

let reconcile_physical_keys () =
    // Peek outside the input lock: Windows may dispatch sent messages here.
    let keyboard_pending = ValueOption.isSome (Win32.queued_keyboard_timestamp ())
    Monitor.Enter state.transition_gate

    try
        if Volatile.Read(&state.active) && not keyboard_pending then
            let mutable changed = false

            for physical_key in state.configured.exact do
                if release_stale_key physical_key then
                    changed <- true

            if state.configured.either_shift then
                if release_stale_key Win32Native.VK_LSHIFT then
                    changed <- true

                if release_stale_key Win32Native.VK_RSHIFT then
                    changed <- true

            if state.configured.either_control then
                if release_stale_key Win32Native.VK_LCONTROL then
                    changed <- true

                if release_stale_key Win32Native.VK_RCONTROL then
                    changed <- true

            if state.configured.either_alt then
                if release_stale_key Win32Native.VK_LMENU then
                    changed <- true

                if release_stale_key Win32Native.VK_RMENU then
                    changed <- true

            if changed then
                Interlocked.Increment(&state.revision) |> ignore

                match state.input_available with
                | Some available -> available.Invoke()
                | None -> ()
    finally
        Monitor.Exit state.transition_gate

let hook_event (event: Win32.KeyboardHookEvent) =
    let mutable swallow = false
    Monitor.Enter state.transition_gate

    try
        try
            let escape_up_without_down =
                event.physical_key = Win32Native.VK_ESCAPE
                && event.released
                && Volatile.Read(&state.active)
                && Volatile.Read(&state.accept_new_keys)
                && not state.observed_key_is_down[event.physical_key]
                && not (state.suppressed_keys_down.Contains event.physical_key)
                && not (state.passthrough_keys_down.Contains event.physical_key)

            let was_down = state.observed_key_is_down[event.physical_key]
            swallow <- handle_event event

            if escape_up_without_down && try_request_plain_escape_exit () then
                Interlocked.Increment(&state.revision) |> ignore

                match state.input_available with
                | Some available -> available.Invoke()
                | None -> ()
            elif was_down <> state.observed_key_is_down[event.physical_key] then
                let escape_requested =
                    event.physical_key = Win32Native.VK_ESCAPE
                    && not event.released
                    && try_request_plain_escape_exit ()

                match state.input with
                | Some input when not escape_requested ->
                    InputAccumulator.add_keyboard_transition
                        event.timestamp
                        event.physical_key
                        state.observed_key_is_down[event.physical_key]
                        input
                | _ -> ()

                Interlocked.Increment(&state.revision) |> ignore

                match state.input_available with
                | Some available -> available.Invoke()
                | None -> ()

        with error ->
            Debug.WriteLine $"RhinosCanFly keyboard suppression failed: {error}"
    finally
        Monitor.Exit state.transition_gate

    swallow

let apply_keyboard_transition (key: int) (down: bool) =
    state.key_is_down[key] <- down
    collect_actions ()

[<Struct>]
type LogicalMouseTransition =
    { virtual_key: int
      down: bool
      valid: bool }

let logical_mouse_transition (event: RawMouseButtonEvent) =
    match event with
    | RawMouseButtonEvent.LeftDown ->
        { virtual_key = Win32Native.VK_LBUTTON
          down = true
          valid = true }
    | RawMouseButtonEvent.LeftUp ->
        { virtual_key = Win32Native.VK_LBUTTON
          down = false
          valid = true }
    | RawMouseButtonEvent.RightDown ->
        { virtual_key = Win32Native.VK_RBUTTON
          down = true
          valid = true }
    | RawMouseButtonEvent.RightUp ->
        { virtual_key = Win32Native.VK_RBUTTON
          down = false
          valid = true }
    | RawMouseButtonEvent.MiddleDown ->
        { virtual_key = Win32Native.VK_MBUTTON
          down = true
          valid = true }
    | RawMouseButtonEvent.MiddleUp ->
        { virtual_key = Win32Native.VK_MBUTTON
          down = false
          valid = true }
    | RawMouseButtonEvent.Mouse4Down ->
        { virtual_key = Win32Native.VK_XBUTTON1
          down = true
          valid = true }
    | RawMouseButtonEvent.Mouse4Up ->
        { virtual_key = Win32Native.VK_XBUTTON1
          down = false
          valid = true }
    | RawMouseButtonEvent.Mouse5Down ->
        { virtual_key = Win32Native.VK_XBUTTON2
          down = true
          valid = true }
    | RawMouseButtonEvent.Mouse5Up ->
        { virtual_key = Win32Native.VK_XBUTTON2
          down = false
          valid = true }
    | RawMouseButtonEvent.None
    | _ ->
        { virtual_key = 0
          down = false
          valid = false }

let apply_raw_mouse_button_transition_core (transition: RawMouseButtonTransition) =
    let logical = logical_mouse_transition transition.event

    if
        Volatile.Read(&state.active)
        && logical.valid
        && state.mouse_key_configured[logical.virtual_key]
    then
        state.key_is_down[logical.virtual_key] <- logical.down

        collect_actions ()
    else
        InputAccumulator.KeyboardAction.None

let apply_raw_mouse_button_transition (transition: RawMouseButtonTransition) =
    Monitor.Enter state.transition_gate

    try
        apply_raw_mouse_button_transition_core transition
    finally
        Monitor.Exit state.transition_gate

let consume_escape_exit (lifetime: FlightLifetime) (exit_buttons: MouseExitConfig) (input: InputAccumulator.State) =
    if
        Volatile.Read(&input.escape_requested)
        && Option.isNone (InputAccumulator.exit_reason input)
    then
        let events = InputAccumulator.timeline_buffer ()
        let struct (count, overflowed) = InputAccumulator.drain_timeline events input
        let mutable index = 0
        let mutable reason = None

        while index < count && Option.isNone reason do
            let event = events[index]

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

        let final_reason =
            match reason with
            | Some reason -> reason
            | None ->
                match state.bindings with
                | Some bindings when is_plain_escape bindings.cancel_flight_and_restore -> ExplicitRestoreCamera
                | _ when overflowed -> SessionFailure "The input timeline overflowed before Escape."
                | _ -> ExplicitKeepCamera

        InputAccumulator.request_exit final_reason input

let revision () = Volatile.Read(&state.revision)

let movement_boundary () =
    let queued = Win32.queued_keyboard_timestamp ()
    let frame = Stopwatch.GetTimestamp()

    let boundary =
        match queued with
        | ValueSome timestamp -> min frame timestamp
        | ValueNone -> frame

    struct (frame, boundary)

let allow_passthrough () =
    Volatile.Write(&state.accept_new_keys, false)

let ensure_hook () =
    if not escape_handler_installed then
        RhinoApp.EscapeKeyPressed.AddHandler escape_key_pressed
        escape_handler_installed <- true

    match keyboard_hook with
    | Some _ -> Ok()
    | None ->
        match Win32.install_keyboard_hook hook_event with
        | Ok hook ->
            keyboard_hook <- Some hook
            Ok()
        | Error error -> Error error

let start (config: FlyConfig) (input: InputAccumulator.State) (input_available: Action) =
    match ensure_hook () with
    | Error error -> Error error
    | Ok() ->
        try
            let swapped = Win32.mouse_buttons_swapped ()
            Monitor.Enter state.transition_gate

            try
                configure_with_physical_snapshot
                    swapped
                    (fun (key: int) -> Win32Native.GetAsyncKeyState key < 0s)
                    config
                    input
                    input_available
            finally
                Monitor.Exit state.transition_gate

            Ok swapped
        with error ->
            stop ()
            Error error.Message

let shutdown () =
    stop ()

    if escape_handler_installed then
        RhinoApp.EscapeKeyPressed.RemoveHandler escape_key_pressed
        escape_handler_installed <- false

    Monitor.Enter state.transition_gate

    try
        state.suppressed_keys_down.Clear()
        System.Array.Clear(state.key_is_down, 0, state.key_is_down.Length)
    finally
        Monitor.Exit state.transition_gate

    match keyboard_hook with
    | None -> Ok()
    | Some hook ->
        match Win32.remove_hook hook with
        | Ok() ->
            keyboard_hook <- None
            Ok()
        | Error error -> Error error
