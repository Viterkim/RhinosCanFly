module RhinosCanFly.Platform.Win.FlightKeyboardSuppression

open System
open System.Collections.Generic
open System.Diagnostics
open System.Threading
open RhinosCanFly

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
      mouse_key_configured: bool array
      mutable exit_binding: KeyBinding option
      mutable cancel_and_restore_binding: KeyBinding option
      mutable retarget_all_views_binding: KeyBinding option
      mutable retarget_other_views_binding: KeyBinding option
      mutable exit_pressed: int
      mutable cancel_and_restore_pressed: int
      mutable retarget_all_views_pressed: int
      mutable retarget_other_views_pressed: int
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
      mouse_key_configured = Array.zeroCreate 256
      exit_binding = None
      cancel_and_restore_binding = None
      retarget_all_views_binding = None
      retarget_other_views_binding = None
      exit_pressed = 0
      cancel_and_restore_pressed = 0
      retarget_all_views_pressed = 0
      retarget_other_views_pressed = 0
      input_available = None
      revision = 0L
      accept_new_keys = false
      active = false }

let mutable keyboardHook: Win32Native.WindowsHook option = None

let clear_configured () =
    state.configured.exact.Clear()
    state.configured.either_shift <- false
    state.configured.either_control <- false
    state.configured.either_alt <- false

let add_key (key: VirtualKey) =
    let (VirtualKey virtualKey) = key

    match virtualKey with
    | Win32Native.VK_SHIFT -> state.configured.either_shift <- true
    | Win32Native.VK_CONTROL -> state.configured.either_control <- true
    | Win32Native.VK_MENU -> state.configured.either_alt <- true
    | _ -> state.configured.exact.Add virtualKey |> ignore

let add_binding (binding: KeyBinding) =
    for key in binding.virtual_keys do
        add_key key

let add_optional_binding (binding: KeyBinding option) =
    match binding with
    | Some value -> add_binding value
    | None -> ()

let configured_key (physicalKey: int) =
    state.configured.exact.Contains physicalKey
    || (state.configured.either_shift
        && (physicalKey = Win32Native.VK_LSHIFT || physicalKey = Win32Native.VK_RSHIFT))
    || (state.configured.either_control
        && (physicalKey = Win32Native.VK_LCONTROL || physicalKey = Win32Native.VK_RCONTROL))
    || (state.configured.either_alt
        && (physicalKey = Win32Native.VK_LMENU || physicalKey = Win32Native.VK_RMENU))

let add_passthrough_if_down (physicalKey: int) =
    if
        configured_key physicalKey
        && Win32Native.GetAsyncKeyState physicalKey < 0s
        && not (state.suppressed_keys_down.Contains physicalKey)
    then
        state.passthrough_keys_down.Add physicalKey |> ignore
        state.key_is_down[physicalKey] <- true

let configure (bindings: FlightBindings) (retarget: RetargetConfig) (inputAvailable: Action) =
    let releasedKeys = ResizeArray<int>()

    let enabled_binding (mode: RetargetMode) (binding: KeyBinding option) =
        if mode = RetargetMode.Off then None else binding

    let retargetAllViewsBinding =
        enabled_binding retarget.keyboard_all_views bindings.retarget_all_views

    let retargetOtherViewsBinding =
        enabled_binding retarget.keyboard_other_views bindings.retarget_other_views

    Interlocked.Exchange(&state.exit_pressed, 0) |> ignore
    Interlocked.Exchange(&state.cancel_and_restore_pressed, 0) |> ignore
    Interlocked.Exchange(&state.retarget_all_views_pressed, 0) |> ignore
    Interlocked.Exchange(&state.retarget_other_views_pressed, 0) |> ignore
    System.Array.Clear(state.key_is_down, 0, state.key_is_down.Length)

    for physicalKey in state.suppressed_keys_down do
        if Win32Native.GetAsyncKeyState physicalKey >= 0s then
            releasedKeys.Add physicalKey

    for physicalKey in releasedKeys do
        state.suppressed_keys_down.Remove physicalKey |> ignore
        state.key_is_down[physicalKey] <- false

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
    add_optional_binding retargetAllViewsBinding
    add_optional_binding retargetOtherViewsBinding
    add_binding bindings.exit_key
    add_binding bindings.cancel_flight_and_restore
    add_optional_binding bindings.toggle_projection

    state.mouse_key_configured[Win32Native.VK_LBUTTON] <- configured_key Win32Native.VK_LBUTTON
    state.mouse_key_configured[Win32Native.VK_RBUTTON] <- configured_key Win32Native.VK_RBUTTON
    state.mouse_key_configured[Win32Native.VK_MBUTTON] <- configured_key Win32Native.VK_MBUTTON
    state.mouse_key_configured[Win32Native.VK_XBUTTON1] <- configured_key Win32Native.VK_XBUTTON1
    state.mouse_key_configured[Win32Native.VK_XBUTTON2] <- configured_key Win32Native.VK_XBUTTON2

    state.passthrough_keys_down.Clear()

    for physicalKey in state.configured.exact do
        add_passthrough_if_down physicalKey

    if state.configured.either_shift then
        add_passthrough_if_down Win32Native.VK_LSHIFT
        add_passthrough_if_down Win32Native.VK_RSHIFT

    if state.configured.either_control then
        add_passthrough_if_down Win32Native.VK_LCONTROL
        add_passthrough_if_down Win32Native.VK_RCONTROL

    if state.configured.either_alt then
        add_passthrough_if_down Win32Native.VK_LMENU
        add_passthrough_if_down Win32Native.VK_RMENU

    state.input_available <- Some inputAvailable
    state.exit_binding <- Some bindings.exit_key
    state.cancel_and_restore_binding <- Some bindings.cancel_flight_and_restore
    state.retarget_all_views_binding <- retargetAllViewsBinding
    state.retarget_other_views_binding <- retargetOtherViewsBinding
    Volatile.Write(&state.accept_new_keys, true)
    Volatile.Write(&state.active, true)

let stop_core () =
    Volatile.Write(&state.accept_new_keys, false)
    Volatile.Write(&state.active, false)
    state.input_available <- None
    state.exit_binding <- None
    state.cancel_and_restore_binding <- None
    state.retarget_all_views_binding <- None
    state.retarget_other_views_binding <- None
    Interlocked.Exchange(&state.exit_pressed, 0) |> ignore
    Interlocked.Exchange(&state.cancel_and_restore_pressed, 0) |> ignore
    Interlocked.Exchange(&state.retarget_all_views_pressed, 0) |> ignore
    Interlocked.Exchange(&state.retarget_other_views_pressed, 0) |> ignore
    clear_configured ()
    state.passthrough_keys_down.Clear()
    System.Array.Clear(state.key_is_down, 0, state.key_is_down.Length)
    System.Array.Clear(state.mouse_key_configured, 0, state.mouse_key_configured.Length)

let stop () =
    Monitor.Enter state.transition_gate

    try
        stop_core ()
    finally
        Monitor.Exit state.transition_gate

let classify_fresh_key_down (physicalKey: int) =
    if
        Volatile.Read(&state.active)
        && Volatile.Read(&state.accept_new_keys)
        && configured_key physicalKey
    then
        state.suppressed_keys_down.Add physicalKey |> ignore
        state.key_is_down[physicalKey] <- true
        true
    else
        false

let handle_event (event: Win32.KeyboardHookEvent) =
    let physicalKey = event.physical_key

    if not (Volatile.Read(&state.active)) && state.suppressed_keys_down.Count = 0 then
        false
    elif event.released then
        let suppressed = state.suppressed_keys_down.Remove physicalKey
        state.passthrough_keys_down.Remove physicalKey |> ignore
        state.key_is_down[physicalKey] <- false
        suppressed
    elif state.suppressed_keys_down.Contains physicalKey then
        if event.was_down then
            true
        else
            state.suppressed_keys_down.Remove physicalKey |> ignore
            classify_fresh_key_down physicalKey
    elif state.passthrough_keys_down.Contains physicalKey then
        if event.was_down then
            true
        else
            state.passthrough_keys_down.Remove physicalKey |> ignore
            classify_fresh_key_down physicalKey
    else
        classify_fresh_key_down physicalKey

let virtual_key_down (virtualKey: int) =
    match virtualKey with
    | Win32Native.VK_LBUTTON
    | Win32Native.VK_RBUTTON
    | Win32Native.VK_MBUTTON
    | Win32Native.VK_XBUTTON1
    | Win32Native.VK_XBUTTON2 -> Volatile.Read(&state.key_is_down[virtualKey])
    | Win32Native.VK_SHIFT ->
        Volatile.Read(&state.key_is_down[Win32Native.VK_LSHIFT])
        || Volatile.Read(&state.key_is_down[Win32Native.VK_RSHIFT])
    | Win32Native.VK_CONTROL ->
        Volatile.Read(&state.key_is_down[Win32Native.VK_LCONTROL])
        || Volatile.Read(&state.key_is_down[Win32Native.VK_RCONTROL])
    | Win32Native.VK_MENU ->
        Volatile.Read(&state.key_is_down[Win32Native.VK_LMENU])
        || Volatile.Read(&state.key_is_down[Win32Native.VK_RMENU])
    | _ -> Volatile.Read(&state.key_is_down[virtualKey])

let binding_is_down (binding: KeyBinding) =
    if not (Volatile.Read(&state.active)) then
        PlatformBindings.is_down binding
    else
        let keys = binding.virtual_keys
        let mutable index = 0
        let mutable down = keys.Length > 0

        while down && index < keys.Length do
            let (VirtualKey virtualKey) = keys[index]
            down <- virtual_key_down virtualKey
            index <- index + 1

        down

let release_stale_key (physicalKey: int) =
    if
        Volatile.Read(&state.key_is_down[physicalKey])
        && Win32Native.GetAsyncKeyState physicalKey >= 0s
    then
        state.key_is_down[physicalKey] <- false
        state.passthrough_keys_down.Remove physicalKey |> ignore
        true
    else
        false

let reconcile_physical_keys () =
    Monitor.Enter state.transition_gate

    try
        if Volatile.Read(&state.active) then
            let mutable changed = false

            for physicalKey in state.configured.exact do
                if release_stale_key physicalKey then
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
            let wasDown = state.key_is_down[event.physical_key]
            swallow <- handle_event event

            if wasDown <> state.key_is_down[event.physical_key] then
                let mutable exitsFlight = false

                if state.key_is_down[event.physical_key] then
                    match state.exit_binding with
                    | Some binding when binding_is_down binding ->
                        Interlocked.Exchange(&state.exit_pressed, 1) |> ignore
                        exitsFlight <- true
                    | Some _
                    | None -> ()

                    match state.cancel_and_restore_binding with
                    | Some binding when binding_is_down binding ->
                        Interlocked.Exchange(&state.cancel_and_restore_pressed, 1) |> ignore
                        exitsFlight <- true
                    | Some _
                    | None -> ()

                    match state.retarget_all_views_binding with
                    | Some binding when binding_is_down binding ->
                        Interlocked.Exchange(&state.retarget_all_views_pressed, 1) |> ignore
                    | Some _
                    | None -> ()

                    match state.retarget_other_views_binding with
                    | Some binding when binding_is_down binding ->
                        Interlocked.Exchange(&state.retarget_other_views_pressed, 1) |> ignore
                    | Some _
                    | None -> ()

                if exitsFlight then
                    Volatile.Write(&state.accept_new_keys, false)

                Interlocked.Increment(&state.revision) |> ignore

                match state.input_available with
                | Some available -> available.Invoke()
                | None -> ()
        with error ->
            Debug.WriteLine $"RhinosCanFly keyboard suppression failed: {error}"
    finally
        Monitor.Exit state.transition_gate

    swallow

[<Struct>]
type PhysicalMouseTransition =
    { physical_key: int
      down: bool
      valid: bool }

let physical_mouse_transition (event: RawMouseButtonEvent) =
    match event with
    | RawMouseButtonEvent.LeftDown ->
        { physical_key = Win32Native.VK_LBUTTON
          down = true
          valid = true }
    | RawMouseButtonEvent.LeftUp ->
        { physical_key = Win32Native.VK_LBUTTON
          down = false
          valid = true }
    | RawMouseButtonEvent.RightDown ->
        { physical_key = Win32Native.VK_RBUTTON
          down = true
          valid = true }
    | RawMouseButtonEvent.RightUp ->
        { physical_key = Win32Native.VK_RBUTTON
          down = false
          valid = true }
    | RawMouseButtonEvent.MiddleDown ->
        { physical_key = Win32Native.VK_MBUTTON
          down = true
          valid = true }
    | RawMouseButtonEvent.MiddleUp ->
        { physical_key = Win32Native.VK_MBUTTON
          down = false
          valid = true }
    | RawMouseButtonEvent.Mouse4Down ->
        { physical_key = Win32Native.VK_XBUTTON1
          down = true
          valid = true }
    | RawMouseButtonEvent.Mouse4Up ->
        { physical_key = Win32Native.VK_XBUTTON1
          down = false
          valid = true }
    | RawMouseButtonEvent.Mouse5Down ->
        { physical_key = Win32Native.VK_XBUTTON2
          down = true
          valid = true }
    | RawMouseButtonEvent.Mouse5Up ->
        { physical_key = Win32Native.VK_XBUTTON2
          down = false
          valid = true }
    | RawMouseButtonEvent.None
    | _ ->
        { physical_key = 0
          down = false
          valid = false }

let apply_raw_mouse_button_transition_core (transition: RawMouseButtonTransition) =
    let physical = physical_mouse_transition transition.event

    if
        Volatile.Read(&state.active)
        && physical.valid
        && state.mouse_key_configured[physical.physical_key]
    then
        state.key_is_down[physical.physical_key] <- physical.down
        Interlocked.Increment(&state.revision) |> ignore

let apply_raw_mouse_button_transition (transition: RawMouseButtonTransition) =
    Monitor.Enter state.transition_gate

    try
        apply_raw_mouse_button_transition_core transition
    finally
        Monitor.Exit state.transition_gate

let revision () = Volatile.Read(&state.revision)

let allow_passthrough () =
    Volatile.Write(&state.accept_new_keys, false)

let drain_exit_pressed () =
    Interlocked.Exchange(&state.exit_pressed, 0) <> 0

let drain_cancel_and_restore_pressed () =
    Interlocked.Exchange(&state.cancel_and_restore_pressed, 0) <> 0

let drain_retarget_all_views_pressed () =
    Interlocked.Exchange(&state.retarget_all_views_pressed, 0) <> 0

let drain_retarget_other_views_pressed () =
    Interlocked.Exchange(&state.retarget_other_views_pressed, 0) <> 0

let ensure_hook () =
    match keyboardHook with
    | Some _ -> Ok()
    | None ->
        match Win32.install_keyboard_hook hook_event with
        | Ok hook ->
            keyboardHook <- Some hook
            Ok()
        | Error error -> Error error

let start (bindings: FlightBindings) (retarget: RetargetConfig) (inputAvailable: Action) =
    match ensure_hook () with
    | Error error -> Error error
    | Ok() ->
        try
            Monitor.Enter state.transition_gate

            try
                configure bindings retarget inputAvailable
            finally
                Monitor.Exit state.transition_gate

            Ok()
        with error ->
            stop ()
            Error error.Message

let shutdown () =
    stop ()
    Monitor.Enter state.transition_gate

    try
        state.suppressed_keys_down.Clear()
        System.Array.Clear(state.key_is_down, 0, state.key_is_down.Length)
    finally
        Monitor.Exit state.transition_gate

    match keyboardHook with
    | None -> Ok()
    | Some hook ->
        match Win32.remove_hook hook with
        | Ok() ->
            keyboardHook <- None
            Ok()
        | Error error -> Error error
