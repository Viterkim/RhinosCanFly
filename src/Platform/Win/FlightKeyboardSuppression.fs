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
      mutable exit_down: bool
      mutable cancel_and_restore_down: bool
      mutable input: InputAccumulator.State option
      mutable input_available: Action option
      mutable revision: int64
      mutable last_change_timestamp: int64
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
      exit_down = false
      cancel_and_restore_down = false
      input = None
      input_available = None
      revision = 0L
      last_change_timestamp = 0L
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

let virtual_key_down (virtualKey: int) =
    match virtualKey with
    | Win32Native.VK_LBUTTON
    | Win32Native.VK_RBUTTON
    | Win32Native.VK_MBUTTON
    | Win32Native.VK_XBUTTON1
    | Win32Native.VK_XBUTTON2 -> Volatile.Read(&state.key_is_down[virtualKey])
    | Win32Native.VK_SHIFT ->
        state.key_is_down[Win32Native.VK_LSHIFT]
        || state.key_is_down[Win32Native.VK_RSHIFT]
    | Win32Native.VK_CONTROL ->
        state.key_is_down[Win32Native.VK_LCONTROL]
        || state.key_is_down[Win32Native.VK_RCONTROL]
    | Win32Native.VK_MENU ->
        state.key_is_down[Win32Native.VK_LMENU]
        || state.key_is_down[Win32Native.VK_RMENU]
    | _ -> state.key_is_down[virtualKey]

let binding_is_down (binding: KeyBinding) =
    if not state.active then
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

let is_optional_binding_down (binding: KeyBinding option) =
    match binding with
    | Some value -> binding_is_down value
    | None -> false

let configure (config: FlyConfig) (input: InputAccumulator.State) (inputAvailable: Action) =
    let releasedKeys = ResizeArray<int>()
    let bindings = config.bindings
    let retarget = config.behavior.retarget

    let enabled_binding (mode: RetargetMode) (binding: KeyBinding option) =
        if mode = RetargetMode.Off then None else binding

    let retargetAllViewsBinding =
        enabled_binding retarget.keyboard_all_views bindings.retarget_all_views

    let retargetOtherViewsBinding =
        enabled_binding retarget.keyboard_other_views bindings.retarget_other_views

    Volatile.Write(&state.last_change_timestamp, 0L)
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

    state.bindings <- Some bindings
    state.boost_mode <- config.movement.boost_mode
    state.slow_mode <- config.movement.slow_mode
    state.retarget_all_views_binding <- retargetAllViewsBinding
    state.retarget_other_views_binding <- retargetOtherViewsBinding
    state.pivot_toggle_down <- is_optional_binding_down bindings.mouse_navigation.pivot.toggle
    state.pan_toggle_down <- is_optional_binding_down bindings.mouse_navigation.pan.toggle
    state.pivot_hold_down <- is_optional_binding_down bindings.mouse_navigation.pivot.hold
    state.pan_hold_down <- is_optional_binding_down bindings.mouse_navigation.pan.hold
    state.boost_down <- binding_is_down bindings.boost
    state.slow_down <- binding_is_down bindings.slow
    state.speed_increase_down <- is_optional_binding_down bindings.speed_increase
    state.speed_decrease_down <- is_optional_binding_down bindings.speed_decrease
    state.projection_toggle_down <- is_optional_binding_down bindings.toggle_projection
    state.retarget_all_views_down <- is_optional_binding_down retargetAllViewsBinding
    state.retarget_other_views_down <- is_optional_binding_down retargetOtherViewsBinding
    state.exit_down <- binding_is_down bindings.exit_key
    state.cancel_and_restore_down <- binding_is_down bindings.cancel_flight_and_restore
    state.input <- Some input
    state.input_available <- Some inputAvailable
    Volatile.Write(&state.active, true)

let stop_core () =
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
    state.exit_down <- false
    state.cancel_and_restore_down <- false
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
    if state.active && configured_key physicalKey then
        state.suppressed_keys_down.Add physicalKey |> ignore
        state.key_is_down[physicalKey] <- true
        true
    else
        false

let handle_event (event: Win32.KeyboardHookEvent) =
    let physicalKey = event.physical_key

    if not state.active && state.suppressed_keys_down.Count = 0 then
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

let add_action (current: InputAccumulator.KeyboardAction) (added: InputAccumulator.KeyboardAction) =
    enum<InputAccumulator.KeyboardAction> (int current ||| int added)

let collect_actions () =
    match state.bindings with
    | None -> InputAccumulator.KeyboardAction.None
    | Some bindings ->
        let mutable actions = InputAccumulator.KeyboardAction.None

        let pivotToggle = is_optional_binding_down bindings.mouse_navigation.pivot.toggle

        if pivotToggle && not state.pivot_toggle_down then
            actions <- add_action actions InputAccumulator.KeyboardAction.PivotToggle

        state.pivot_toggle_down <- pivotToggle

        let panToggle = is_optional_binding_down bindings.mouse_navigation.pan.toggle

        if panToggle && not state.pan_toggle_down then
            actions <- add_action actions InputAccumulator.KeyboardAction.PanToggle

        state.pan_toggle_down <- panToggle

        let pivotHold = is_optional_binding_down bindings.mouse_navigation.pivot.hold

        if pivotHold <> state.pivot_hold_down then
            actions <-
                add_action
                    actions
                    (if pivotHold then
                         InputAccumulator.KeyboardAction.PivotHoldStarted
                     else
                         InputAccumulator.KeyboardAction.PivotHoldEnded)

        state.pivot_hold_down <- pivotHold

        let panHold = is_optional_binding_down bindings.mouse_navigation.pan.hold

        if panHold <> state.pan_hold_down then
            actions <-
                add_action
                    actions
                    (if panHold then
                         InputAccumulator.KeyboardAction.PanHoldStarted
                     else
                         InputAccumulator.KeyboardAction.PanHoldEnded)

        state.pan_hold_down <- panHold

        let boost = binding_is_down bindings.boost

        if boost && not state.boost_down && state.boost_mode = KeyActivationMode.Toggle then
            actions <- add_action actions InputAccumulator.KeyboardAction.BoostToggle

        state.boost_down <- boost

        let slow = binding_is_down bindings.slow

        if slow && not state.slow_down && state.slow_mode = KeyActivationMode.Toggle then
            actions <- add_action actions InputAccumulator.KeyboardAction.SlowToggle

        state.slow_down <- slow

        let speedIncrease = is_optional_binding_down bindings.speed_increase

        if speedIncrease && not state.speed_increase_down then
            actions <- add_action actions InputAccumulator.KeyboardAction.SpeedIncrease

        state.speed_increase_down <- speedIncrease

        let speedDecrease = is_optional_binding_down bindings.speed_decrease

        if speedDecrease && not state.speed_decrease_down then
            actions <- add_action actions InputAccumulator.KeyboardAction.SpeedDecrease

        state.speed_decrease_down <- speedDecrease

        let projectionToggle = is_optional_binding_down bindings.toggle_projection

        if projectionToggle && not state.projection_toggle_down then
            actions <- add_action actions InputAccumulator.KeyboardAction.ProjectionToggle

        state.projection_toggle_down <- projectionToggle

        let retargetAll = is_optional_binding_down state.retarget_all_views_binding

        if retargetAll && not state.retarget_all_views_down then
            actions <- add_action actions InputAccumulator.KeyboardAction.RetargetAllViews

        state.retarget_all_views_down <- retargetAll

        let retargetOther = is_optional_binding_down state.retarget_other_views_binding

        if retargetOther && not state.retarget_other_views_down then
            actions <- add_action actions InputAccumulator.KeyboardAction.RetargetOtherViews

        state.retarget_other_views_down <- retargetOther

        let exit = binding_is_down bindings.exit_key

        if exit && not state.exit_down then
            actions <- add_action actions InputAccumulator.KeyboardAction.Exit

        state.exit_down <- exit

        let cancelAndRestore = binding_is_down bindings.cancel_flight_and_restore

        if cancelAndRestore && not state.cancel_and_restore_down then
            actions <- add_action actions InputAccumulator.KeyboardAction.CancelAndRestore

        state.cancel_and_restore_down <- cancelAndRestore
        actions

let hook_event (event: Win32.KeyboardHookEvent) =
    let mutable swallow = false
    Monitor.Enter state.transition_gate

    try
        try
            let wasDown = state.key_is_down[event.physical_key]
            swallow <- handle_event event

            if wasDown <> state.key_is_down[event.physical_key] then
                let timestamp = Stopwatch.GetTimestamp()
                let actions = collect_actions ()

                match state.input with
                | Some input -> InputAccumulator.add_keyboard_actions actions timestamp input
                | None -> ()

                Volatile.Write(&state.last_change_timestamp, timestamp)
                Interlocked.Increment(&state.revision) |> ignore

                match state.input_available with
                | Some available -> available.Invoke()
                | None -> ()
        with error ->
            Debug.WriteLine $"RhinosCanFly keyboard suppression failed: {error}"
    finally
        Monitor.Exit state.transition_gate

    swallow

let apply_raw_mouse_button_transition_core (transition: RawMouseButtonTransition) =
    let mutable physicalKey = 0
    let mutable down = false

    match transition.event with
    | RawMouseButtonEvent.LeftDown ->
        physicalKey <- Win32Native.VK_LBUTTON
        down <- true
    | RawMouseButtonEvent.LeftUp -> physicalKey <- Win32Native.VK_LBUTTON
    | RawMouseButtonEvent.RightDown ->
        physicalKey <- Win32Native.VK_RBUTTON
        down <- true
    | RawMouseButtonEvent.RightUp -> physicalKey <- Win32Native.VK_RBUTTON
    | RawMouseButtonEvent.MiddleDown ->
        physicalKey <- Win32Native.VK_MBUTTON
        down <- true
    | RawMouseButtonEvent.MiddleUp -> physicalKey <- Win32Native.VK_MBUTTON
    | RawMouseButtonEvent.Mouse4Down ->
        physicalKey <- Win32Native.VK_XBUTTON1
        down <- true
    | RawMouseButtonEvent.Mouse4Up -> physicalKey <- Win32Native.VK_XBUTTON1
    | RawMouseButtonEvent.Mouse5Down ->
        physicalKey <- Win32Native.VK_XBUTTON2
        down <- true
    | RawMouseButtonEvent.Mouse5Up -> physicalKey <- Win32Native.VK_XBUTTON2
    | RawMouseButtonEvent.None
    | _ -> ()

    if state.active && physicalKey <> 0 && state.mouse_key_configured[physicalKey] then
        state.key_is_down[physicalKey] <- down
        Volatile.Write(&state.last_change_timestamp, transition.timestamp)

        collect_actions ()
    else
        InputAccumulator.KeyboardAction.None

let apply_raw_mouse_button_transition (transition: RawMouseButtonTransition) =
    Monitor.Enter state.transition_gate

    try
        apply_raw_mouse_button_transition_core transition
    finally
        Monitor.Exit state.transition_gate

let revision () = Volatile.Read(&state.revision)

let last_change_timestamp () =
    Volatile.Read(&state.last_change_timestamp)

let ensure_hook () =
    match keyboardHook with
    | Some _ -> Ok()
    | None ->
        match Win32.install_keyboard_hook hook_event with
        | Ok hook ->
            keyboardHook <- Some hook
            Ok()
        | Error error -> Error error

let start (config: FlyConfig) (input: InputAccumulator.State) (inputAvailable: Action) =
    match ensure_hook () with
    | Error error -> Error error
    | Ok() ->
        try
            Monitor.Enter state.transition_gate

            try
                configure config input inputAvailable
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
