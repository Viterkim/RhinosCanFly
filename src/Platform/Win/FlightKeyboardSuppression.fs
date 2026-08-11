module RhinosCanFly.Platform.Win.FlightKeyboardSuppression

open System.Collections.Generic
open RhinosCanFly

type State = { keys: HashSet<int> }

let create () = { keys = HashSet<int>() }

let add_key (state: State) (key: VirtualKey) =
    let (VirtualKey virtualKey) = key

    let add (value: int) = state.keys.Add value |> ignore

    match virtualKey with
    | Win32Native.VK_SHIFT
    | Win32Native.VK_LSHIFT
    | Win32Native.VK_RSHIFT ->
        add Win32Native.VK_SHIFT
        add Win32Native.VK_LSHIFT
        add Win32Native.VK_RSHIFT
    | Win32Native.VK_CONTROL
    | Win32Native.VK_LCONTROL
    | Win32Native.VK_RCONTROL ->
        add Win32Native.VK_CONTROL
        add Win32Native.VK_LCONTROL
        add Win32Native.VK_RCONTROL
    | Win32Native.VK_MENU
    | Win32Native.VK_LMENU
    | Win32Native.VK_RMENU ->
        add Win32Native.VK_MENU
        add Win32Native.VK_LMENU
        add Win32Native.VK_RMENU
    | _ -> add virtualKey

let add_binding (state: State) (binding: KeyBinding) =
    binding.virtual_keys |> List.iter (add_key state)

let add_optional_binding (state: State) (binding: KeyBinding option) =
    binding |> Option.iter (add_binding state)

let start (bindings: FlightBindings) (state: State) =
    state.keys.Clear()
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

let stop (state: State) = state.keys.Clear()

let contains (virtualKey: int) (state: State) = state.keys.Contains virtualKey
