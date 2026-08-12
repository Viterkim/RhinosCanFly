module RhinosCanFly.Platform.Win.FlightKeyboardSuppression

open System.Collections.Generic
open RhinosCanFly

type ConfiguredKeys =
    { exact: HashSet<int>
      mutable either_shift: bool
      mutable either_control: bool
      mutable either_alt: bool }

type State =
    { configured: ConfiguredKeys
      passthrough_keys_down: HashSet<int>
      suppressed_keys_down: HashSet<int>
      release_observed_keys: HashSet<int>
      released_keys: ResizeArray<int>
      mutable active: bool }

let create () =
    { configured =
        { exact = HashSet<int>()
          either_shift = false
          either_control = false
          either_alt = false }
      passthrough_keys_down = HashSet<int>()
      suppressed_keys_down = HashSet<int>()
      release_observed_keys = HashSet<int>()
      released_keys = ResizeArray<int>()
      active = false }

let clear_configured (state: State) =
    state.configured.exact.Clear()
    state.configured.either_shift <- false
    state.configured.either_control <- false
    state.configured.either_alt <- false

let add_key (state: State) (key: VirtualKey) =
    let (VirtualKey virtualKey) = key

    match virtualKey with
    | Win32Native.VK_SHIFT -> state.configured.either_shift <- true
    | Win32Native.VK_CONTROL -> state.configured.either_control <- true
    | Win32Native.VK_MENU -> state.configured.either_alt <- true
    | _ -> state.configured.exact.Add virtualKey |> ignore

let add_binding (state: State) (binding: KeyBinding) =
    binding.virtual_keys |> List.iter (add_key state)

let add_optional_binding (state: State) (binding: KeyBinding option) =
    binding |> Option.iter (add_binding state)

let configured_key (state: State) (physicalKey: int) =
    state.configured.exact.Contains physicalKey
    || (state.configured.either_shift
        && (physicalKey = Win32Native.VK_LSHIFT || physicalKey = Win32Native.VK_RSHIFT))
    || (state.configured.either_control
        && (physicalKey = Win32Native.VK_LCONTROL || physicalKey = Win32Native.VK_RCONTROL))
    || (state.configured.either_alt
        && (physicalKey = Win32Native.VK_LMENU || physicalKey = Win32Native.VK_RMENU))

let add_passthrough_if_down (state: State) (physicalKey: int) =
    if
        configured_key state physicalKey
        && Win32Native.GetAsyncKeyState physicalKey < 0s
        && not (state.suppressed_keys_down.Contains physicalKey)
    then
        state.passthrough_keys_down.Add physicalKey |> ignore

let start (bindings: FlightBindings) (state: State) =
    clear_configured state
    state.release_observed_keys.Clear()
    state.active <- true
    add_binding state bindings.forward
    add_binding state bindings.backward
    add_binding state bindings.left
    add_binding state bindings.right
    add_binding state bindings.up
    add_binding state bindings.down
    add_binding state bindings.key_pivot_left
    add_binding state bindings.key_pivot_right
    add_optional_binding state bindings.mouse_navigation.pivot.toggle
    add_optional_binding state bindings.mouse_navigation.pivot.hold
    add_optional_binding state bindings.mouse_navigation.pan.toggle
    add_optional_binding state bindings.mouse_navigation.pan.hold
    add_binding state bindings.boost
    add_binding state bindings.slow
    add_optional_binding state bindings.speed_increase
    add_optional_binding state bindings.speed_decrease
    add_binding state bindings.exit_key
    add_binding state bindings.cancel_flight_and_restore

    state.passthrough_keys_down.Clear()

    for physicalKey in state.configured.exact do
        add_passthrough_if_down state physicalKey

    if state.configured.either_shift then
        add_passthrough_if_down state Win32Native.VK_LSHIFT
        add_passthrough_if_down state Win32Native.VK_RSHIFT

    if state.configured.either_control then
        add_passthrough_if_down state Win32Native.VK_LCONTROL
        add_passthrough_if_down state Win32Native.VK_RCONTROL

    if state.configured.either_alt then
        add_passthrough_if_down state Win32Native.VK_LMENU
        add_passthrough_if_down state Win32Native.VK_RMENU

let stop (state: State) =
    state.active <- false
    clear_configured state
    state.passthrough_keys_down.Clear()

let reset (state: State) =
    stop state
    state.suppressed_keys_down.Clear()
    state.release_observed_keys.Clear()
    state.released_keys.Clear()

let own_key_down (physicalKey: int) (state: State) =
    state.release_observed_keys.Remove physicalKey |> ignore
    state.suppressed_keys_down.Add physicalKey |> ignore

let suppress_key_down (physicalKey: int) (state: State) = own_key_down physicalKey state

let classify_fresh_key_down (physicalKey: int) (state: State) =
    if state.active && configured_key state physicalKey then
        own_key_down physicalKey state
        true
    else
        false

let handle_event (event: Win32.KeyboardHookEvent) (state: State) =
    let physicalKey = event.physical_key

    if event.released then
        state.release_observed_keys.Remove physicalKey |> ignore

        if state.suppressed_keys_down.Remove physicalKey then
            state.passthrough_keys_down.Remove physicalKey |> ignore
            true
        else
            state.passthrough_keys_down.Remove physicalKey |> ignore
            false
    elif state.suppressed_keys_down.Contains physicalKey then
        if event.was_down then
            true
        else
            state.suppressed_keys_down.Remove physicalKey |> ignore
            state.release_observed_keys.Remove physicalKey |> ignore
            classify_fresh_key_down physicalKey state
    elif state.passthrough_keys_down.Contains physicalKey then
        if event.was_down then
            true
        else
            state.passthrough_keys_down.Remove physicalKey |> ignore
            classify_fresh_key_down physicalKey state
    else
        classify_fresh_key_down physicalKey state

let requires_hook (state: State) =
    state.active || state.suppressed_keys_down.Count > 0

let is_active (state: State) = state.active

let prune_released_keys (state: State) =
    state.released_keys.Clear()

    for virtualKey in state.suppressed_keys_down do
        if Win32Native.GetAsyncKeyState virtualKey < 0s then
            state.release_observed_keys.Remove virtualKey |> ignore
        elif state.release_observed_keys.Contains virtualKey then
            state.released_keys.Add virtualKey
        else
            state.release_observed_keys.Add virtualKey |> ignore

    for virtualKey in state.released_keys do
        state.release_observed_keys.Remove virtualKey |> ignore
        state.suppressed_keys_down.Remove virtualKey |> ignore

let waiting_for_releases (state: State) =
    not state.active && state.suppressed_keys_down.Count > 0
