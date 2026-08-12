module RhinosCanFly.Platform.Win.FlightKeyboardSuppression

open System.Collections.Generic
open RhinosCanFly

type State =
    { configured_keys: HashSet<int>
      passthrough_keys_down: HashSet<int>
      suppressed_keys_down: HashSet<int>
      release_observed_keys: HashSet<int>
      released_keys: ResizeArray<int>
      mutable active: bool }

let create () =
    { configured_keys = HashSet<int>()
      passthrough_keys_down = HashSet<int>()
      suppressed_keys_down = HashSet<int>()
      release_observed_keys = HashSet<int>()
      released_keys = ResizeArray<int>()
      active = false }

let canonical_key (virtualKey: int) =
    match virtualKey with
    | Win32Native.VK_SHIFT
    | Win32Native.VK_LSHIFT
    | Win32Native.VK_RSHIFT -> Win32Native.VK_SHIFT
    | Win32Native.VK_CONTROL
    | Win32Native.VK_LCONTROL
    | Win32Native.VK_RCONTROL -> Win32Native.VK_CONTROL
    | Win32Native.VK_MENU
    | Win32Native.VK_LMENU
    | Win32Native.VK_RMENU -> Win32Native.VK_MENU
    | _ -> virtualKey

let add_key (state: State) (key: VirtualKey) =
    let (VirtualKey virtualKey) = key
    state.configured_keys.Add(canonical_key virtualKey) |> ignore

let add_binding (state: State) (binding: KeyBinding) =
    binding.virtual_keys |> List.iter (add_key state)

let add_optional_binding (state: State) (binding: KeyBinding option) =
    binding |> Option.iter (add_binding state)

let add_passthrough_if_down (state: State) (physicalKey: int) =
    if
        Win32Native.GetAsyncKeyState physicalKey < 0s
        && not (state.suppressed_keys_down.Contains physicalKey)
    then
        state.passthrough_keys_down.Add physicalKey |> ignore

let add_configured_passthrough (state: State) (configuredKey: int) =
    match configuredKey with
    | Win32Native.VK_SHIFT ->
        add_passthrough_if_down state Win32Native.VK_LSHIFT
        add_passthrough_if_down state Win32Native.VK_RSHIFT
    | Win32Native.VK_CONTROL ->
        add_passthrough_if_down state Win32Native.VK_LCONTROL
        add_passthrough_if_down state Win32Native.VK_RCONTROL
    | Win32Native.VK_MENU ->
        add_passthrough_if_down state Win32Native.VK_LMENU
        add_passthrough_if_down state Win32Native.VK_RMENU
    | _ -> add_passthrough_if_down state configuredKey

let start (bindings: FlightBindings) (state: State) =
    state.configured_keys.Clear()
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

    for configuredKey in state.configured_keys do
        add_configured_passthrough state configuredKey

let stop (state: State) =
    state.active <- false
    state.configured_keys.Clear()
    state.passthrough_keys_down.Clear()

let reset (state: State) =
    stop state
    state.suppressed_keys_down.Clear()
    state.release_observed_keys.Clear()
    state.released_keys.Clear()

let suppress_key_down (physicalKey: int) (state: State) =
    state.release_observed_keys.Remove physicalKey |> ignore
    state.suppressed_keys_down.Add physicalKey |> ignore

let handle_event (event: Win32.KeyboardHookEvent) (state: State) =
    let configuredKey = canonical_key event.virtual_key
    let physicalKey = event.physical_key

    if event.released then
        state.release_observed_keys.Remove physicalKey |> ignore
        state.passthrough_keys_down.Remove physicalKey |> ignore
        let suppressed = state.suppressed_keys_down.Remove physicalKey

        suppressed
    elif state.passthrough_keys_down.Contains physicalKey then
        if not event.was_down then
            state.passthrough_keys_down.Remove physicalKey |> ignore
            suppress_key_down physicalKey state

        true
    elif state.active && state.configured_keys.Contains configuredKey then
        suppress_key_down physicalKey state
        true
    else
        false

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
