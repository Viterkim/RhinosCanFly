module RhinosCanFly.FlightBindingActions

let iter_bindings (visit: string -> KeyBinding -> unit) (config: FlyConfig) =
    let keys = config.bindings

    for name, binding in
        [ "forward", keys.forward
          "backward", keys.backward
          "left", keys.left
          "right", keys.right
          "up", keys.up
          "down", keys.down
          "key_pivot_left", keys.key_pivot_left
          "key_pivot_right", keys.key_pivot_right
          "boost", keys.boost
          "slow", keys.slow
          "exit_key", keys.exit_key
          "cancel_flight_and_restore", keys.cancel_flight_and_restore ] do
        visit name binding

    for name, binding in
        [ "pivot_toggle", keys.mouse_navigation.pivot.toggle
          "pivot_hold", keys.mouse_navigation.pivot.hold
          "pan_toggle", keys.mouse_navigation.pan.toggle
          "pan_hold", keys.mouse_navigation.pan.hold
          "speed_increase", keys.speed_increase
          "speed_decrease", keys.speed_decrease
          "toggle_projection", keys.toggle_projection
          "untilt_view", keys.untilt_view
          "retarget_all_views",
          (if config.behavior.retarget.keyboard_all_views = RetargetMode.Off then
               None
           else
               keys.retarget_all_views)
          "retarget_other_views",
          (if config.behavior.retarget.keyboard_other_views = RetargetMode.Off then
               None
           else
               keys.retarget_other_views) ] do
        Option.iter (visit name) binding

let validate (config: FlyConfig) =
    iter_bindings
        (fun (name: string) (binding: KeyBinding) ->
            PlatformBindings.execution_error binding
            |> Option.iter (fun (error: string) -> invalidOp $"{name}: {error}"))
        config

type State =
    { bindings: KeyBinding array
      down_actions: InputAccumulator.KeyboardAction array
      up_actions: InputAccumulator.KeyboardAction array
      was_down: bool array }

let create (config: FlyConfig) (is_down: KeyBinding -> bool) =
    let bindings = config.bindings

    let actions =
        ResizeArray<KeyBinding * InputAccumulator.KeyboardAction * InputAccumulator.KeyboardAction>()

    let add (binding: KeyBinding option) (down: InputAccumulator.KeyboardAction) (up: InputAccumulator.KeyboardAction) =
        binding
        |> Option.iter (fun (binding: KeyBinding) -> actions.Add(binding, down, up))

    let toggle (binding: KeyBinding option) (action: InputAccumulator.KeyboardAction) =
        add binding action InputAccumulator.KeyboardAction.None

    toggle bindings.mouse_navigation.pivot.toggle InputAccumulator.KeyboardAction.PivotToggle
    toggle bindings.mouse_navigation.pan.toggle InputAccumulator.KeyboardAction.PanToggle

    add
        bindings.mouse_navigation.pivot.hold
        InputAccumulator.KeyboardAction.PivotHoldStarted
        InputAccumulator.KeyboardAction.PivotHoldEnded

    add
        bindings.mouse_navigation.pan.hold
        InputAccumulator.KeyboardAction.PanHoldStarted
        InputAccumulator.KeyboardAction.PanHoldEnded

    if config.movement.boost_mode = KeyActivationMode.Toggle then
        toggle (Some bindings.boost) InputAccumulator.KeyboardAction.BoostToggle

    if config.movement.slow_mode = KeyActivationMode.Toggle then
        toggle (Some bindings.slow) InputAccumulator.KeyboardAction.SlowToggle

    toggle bindings.speed_increase InputAccumulator.KeyboardAction.SpeedIncrease
    toggle bindings.speed_decrease InputAccumulator.KeyboardAction.SpeedDecrease
    toggle bindings.toggle_projection InputAccumulator.KeyboardAction.ProjectionToggle

    if config.behavior.retarget.keyboard_all_views <> RetargetMode.Off then
        toggle bindings.retarget_all_views InputAccumulator.KeyboardAction.RetargetAllViews

    if config.behavior.retarget.keyboard_other_views <> RetargetMode.Off then
        toggle bindings.retarget_other_views InputAccumulator.KeyboardAction.RetargetOtherViews

    toggle bindings.untilt_view InputAccumulator.KeyboardAction.UntiltView
    toggle (Some bindings.exit_key) InputAccumulator.KeyboardAction.Exit
    toggle (Some bindings.cancel_flight_and_restore) InputAccumulator.KeyboardAction.CancelAndRestore

    { bindings =
        actions
        |> Seq.map
            (fun (binding: KeyBinding, _: InputAccumulator.KeyboardAction, _: InputAccumulator.KeyboardAction) ->
                binding)
        |> Seq.toArray
      down_actions =
        actions
        |> Seq.map (fun (_: KeyBinding, down: InputAccumulator.KeyboardAction, _: InputAccumulator.KeyboardAction) ->
            down)
        |> Seq.toArray
      up_actions =
        actions
        |> Seq.map (fun (_: KeyBinding, _: InputAccumulator.KeyboardAction, up: InputAccumulator.KeyboardAction) -> up)
        |> Seq.toArray
      was_down =
        actions
        |> Seq.map
            (fun (binding: KeyBinding, _: InputAccumulator.KeyboardAction, _: InputAccumulator.KeyboardAction) ->
                is_down binding)
        |> Seq.toArray }

let collect (is_down: KeyBinding -> bool) (state: State) =
    let mutable actions = 0

    for index = 0 to state.bindings.Length - 1 do
        let down = is_down state.bindings[index]

        if down <> state.was_down[index] then
            actions <-
                actions
                ||| int (
                    if down then
                        state.down_actions[index]
                    else
                        state.up_actions[index]
                )

            state.was_down[index] <- down

    enum<InputAccumulator.KeyboardAction> actions
