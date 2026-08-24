namespace RhinosCanFly.Platform.Win

open System
open System.Diagnostics
open RhinosCanFly

type RawInputSession
    (
        config: RawInputConfig,
        sessionMode: FlightSessionMode,
        input: InputAccumulator.State,
        inputAvailable: Action,
        runtimeFailed: Action<exn>
    ) =

    [<Literal>]
    let MIDDLE_BUTTON_BIT = 1

    [<Literal>]
    let MOUSE4_BUTTON_BIT = 2

    [<Literal>]
    let MOUSE5_BUTTON_BIT = 4

    // Decode these once so the packet path stays on flat values.
    let middleTogglePivot = RoutedMouseAction.toggles_pivot config.middle_mouse_action
    let middleHoldPivot = RoutedMouseAction.holds_pivot config.middle_mouse_action
    let middleTogglePan = RoutedMouseAction.toggles_pan config.middle_mouse_action
    let middleHoldPan = RoutedMouseAction.holds_pan config.middle_mouse_action

    let middleRetargetMode =
        int (RoutedMouseAction.retarget_mode config.middle_mouse_action)

    let mouse4TogglePivot = RoutedMouseAction.toggles_pivot config.mouse4_action
    let mouse5TogglePivot = RoutedMouseAction.toggles_pivot config.mouse5_action
    let mouse4HoldPivot = RoutedMouseAction.holds_pivot config.mouse4_action
    let mouse5HoldPivot = RoutedMouseAction.holds_pivot config.mouse5_action
    let mouse4TogglePan = RoutedMouseAction.toggles_pan config.mouse4_action
    let mouse5TogglePan = RoutedMouseAction.toggles_pan config.mouse5_action
    let mouse4HoldPan = RoutedMouseAction.holds_pan config.mouse4_action
    let mouse5HoldPan = RoutedMouseAction.holds_pan config.mouse5_action
    let mouse4RetargetMode = int (RoutedMouseAction.retarget_mode config.mouse4_action)
    let mouse5RetargetMode = int (RoutedMouseAction.retarget_mode config.mouse5_action)

    let exitsWhenRightMouseReleased =
        match sessionMode.lifetime with
        | FlightLifetime.WhileRightMouseHeld -> true
        | FlightLifetime.UntilExit -> false

    let rawMouseButtonTransitionFlags =
        RawInputNative.LEFT_BUTTON_DOWN
        ||| RawInputNative.LEFT_BUTTON_UP
        ||| RawInputNative.RIGHT_BUTTON_DOWN
        ||| RawInputNative.RIGHT_BUTTON_UP
        ||| RawInputNative.MIDDLE_BUTTON_DOWN
        ||| RawInputNative.MIDDLE_BUTTON_UP
        ||| RawInputNative.BUTTON_4_DOWN
        ||| RawInputNative.BUTTON_4_UP
        ||| RawInputNative.BUTTON_5_DOWN
        ||| RawInputNative.BUTTON_5_UP

    let initial_held_bit (configured: bool) (virtualKey: int) (buttonBit: int) =
        if configured && Win32Native.GetAsyncKeyState virtualKey < 0s then
            buttonBit
        else
            0

    let current_held_pivot_buttons () =
        let middle =
            initial_held_bit middleHoldPivot Win32Native.VK_MBUTTON MIDDLE_BUTTON_BIT

        let mouse4 =
            initial_held_bit mouse4HoldPivot Win32Native.VK_XBUTTON1 MOUSE4_BUTTON_BIT

        let mouse5 =
            initial_held_bit mouse5HoldPivot Win32Native.VK_XBUTTON2 MOUSE5_BUTTON_BIT

        middle ||| mouse4 ||| mouse5

    let current_held_pan_buttons () =
        let middle = initial_held_bit middleHoldPan Win32Native.VK_MBUTTON MIDDLE_BUTTON_BIT

        let mouse4 =
            initial_held_bit mouse4HoldPan Win32Native.VK_XBUTTON1 MOUSE4_BUTTON_BIT

        let mouse5 =
            initial_held_bit mouse5HoldPan Win32Native.VK_XBUTTON2 MOUSE5_BUTTON_BIT

        middle ||| mouse4 ||| mouse5

    let mutable heldPivotButtons = 0
    let mutable heldPanButtons = 0

    let update_held_pivot (configured: bool) (buttonBit: int) (downFlag: uint16) (upFlag: uint16) (flags: uint16) =
        if configured then
            if flags &&& downFlag <> 0us then
                heldPivotButtons <- heldPivotButtons ||| buttonBit

            if flags &&& upFlag <> 0us then
                heldPivotButtons <- heldPivotButtons &&& (~~~buttonBit)

            flags &&& (downFlag ||| upFlag) <> 0us
        else
            false

    let update_held_pan (configured: bool) (buttonBit: int) (downFlag: uint16) (upFlag: uint16) (flags: uint16) =
        if configured then
            if flags &&& downFlag <> 0us then
                heldPanButtons <- heldPanButtons ||| buttonBit

            if flags &&& upFlag <> 0us then
                heldPanButtons <- heldPanButtons &&& (~~~buttonBit)

            flags &&& (downFlag ||| upFlag) <> 0us
        else
            false

    member _.InitializeHeldButtons() =
        heldPivotButtons <- current_held_pivot_buttons ()
        heldPanButtons <- current_held_pan_buttons ()
        InputAccumulator.set_pivot_held (heldPivotButtons <> 0) input
        InputAccumulator.set_pan_held (heldPanButtons <> 0) input

    member _.ProcessMouse(mouse: RawInputNative.Mouse) =
        let mouseMoved =
            mouse.flags &&& RawInputNative.MOUSE_MOVE_ABSOLUTE = 0us
            && (mouse.last_x <> 0 || mouse.last_y <> 0)

        if mouseMoved then
            InputAccumulator.add_mouse mouse.last_x mouse.last_y input

        let flags = RawInputNative.button_flags mouse

        let buttonTimestamp =
            if flags &&& rawMouseButtonTransitionFlags <> 0us then
                Stopwatch.GetTimestamp()
            else
                0L

        let mutable rawButtonEventAdded = false

        if flags &&& RawInputNative.LEFT_BUTTON_DOWN <> 0us then
            InputAccumulator.add_raw_mouse_button_event RawMouseButtonEvent.LeftDown buttonTimestamp input
            rawButtonEventAdded <- true

        if flags &&& RawInputNative.LEFT_BUTTON_UP <> 0us then
            InputAccumulator.add_raw_mouse_button_event RawMouseButtonEvent.LeftUp buttonTimestamp input
            rawButtonEventAdded <- true

        if flags &&& RawInputNative.RIGHT_BUTTON_DOWN <> 0us then
            InputAccumulator.add_raw_mouse_button_event RawMouseButtonEvent.RightDown buttonTimestamp input
            rawButtonEventAdded <- true

        if flags &&& RawInputNative.RIGHT_BUTTON_UP <> 0us then
            InputAccumulator.add_raw_mouse_button_event RawMouseButtonEvent.RightUp buttonTimestamp input
            rawButtonEventAdded <- true

        if flags &&& RawInputNative.MIDDLE_BUTTON_DOWN <> 0us then
            InputAccumulator.add_raw_mouse_button_event RawMouseButtonEvent.MiddleDown buttonTimestamp input
            rawButtonEventAdded <- true

        if flags &&& RawInputNative.MIDDLE_BUTTON_UP <> 0us then
            InputAccumulator.add_raw_mouse_button_event RawMouseButtonEvent.MiddleUp buttonTimestamp input
            rawButtonEventAdded <- true

        if flags &&& RawInputNative.BUTTON_4_DOWN <> 0us then
            InputAccumulator.add_raw_mouse_button_event RawMouseButtonEvent.Mouse4Down buttonTimestamp input
            rawButtonEventAdded <- true

        if flags &&& RawInputNative.BUTTON_4_UP <> 0us then
            InputAccumulator.add_raw_mouse_button_event RawMouseButtonEvent.Mouse4Up buttonTimestamp input
            rawButtonEventAdded <- true

        if flags &&& RawInputNative.BUTTON_5_DOWN <> 0us then
            InputAccumulator.add_raw_mouse_button_event RawMouseButtonEvent.Mouse5Down buttonTimestamp input
            rawButtonEventAdded <- true

        if flags &&& RawInputNative.BUTTON_5_UP <> 0us then
            InputAccumulator.add_raw_mouse_button_event RawMouseButtonEvent.Mouse5Up buttonTimestamp input
            rawButtonEventAdded <- true

        let wheelDelta =
            if flags &&& RawInputNative.MOUSE_WHEEL <> 0us then
                RawInputNative.signed_button_data mouse
            else
                0

        if wheelDelta <> 0 then
            InputAccumulator.add_wheel wheelDelta input

        let middleDown = flags &&& RawInputNative.MIDDLE_BUTTON_DOWN <> 0us
        let mouse4Down = flags &&& RawInputNative.BUTTON_4_DOWN <> 0us
        let mouse5Down = flags &&& RawInputNative.BUTTON_5_DOWN <> 0us

        let middlePivotRequested = middleTogglePivot && middleDown
        let mouse4PivotRequested = mouse4TogglePivot && mouse4Down
        let mouse5PivotRequested = mouse5TogglePivot && mouse5Down
        let middlePanRequested = middleTogglePan && middleDown
        let mouse4PanRequested = mouse4TogglePan && mouse4Down
        let mouse5PanRequested = mouse5TogglePan && mouse5Down

        let middleHeldChanged =
            update_held_pivot
                middleHoldPivot
                MIDDLE_BUTTON_BIT
                RawInputNative.MIDDLE_BUTTON_DOWN
                RawInputNative.MIDDLE_BUTTON_UP
                flags

        let mouse4HeldChanged =
            update_held_pivot
                mouse4HoldPivot
                MOUSE4_BUTTON_BIT
                RawInputNative.BUTTON_4_DOWN
                RawInputNative.BUTTON_4_UP
                flags

        let mouse5HeldChanged =
            update_held_pivot
                mouse5HoldPivot
                MOUSE5_BUTTON_BIT
                RawInputNative.BUTTON_5_DOWN
                RawInputNative.BUTTON_5_UP
                flags

        let middlePanHeldChanged =
            update_held_pan
                middleHoldPan
                MIDDLE_BUTTON_BIT
                RawInputNative.MIDDLE_BUTTON_DOWN
                RawInputNative.MIDDLE_BUTTON_UP
                flags

        let mouse4PanHeldChanged =
            update_held_pan
                mouse4HoldPan
                MOUSE4_BUTTON_BIT
                RawInputNative.BUTTON_4_DOWN
                RawInputNative.BUTTON_4_UP
                flags

        let mouse5PanHeldChanged =
            update_held_pan
                mouse5HoldPan
                MOUSE5_BUTTON_BIT
                RawInputNative.BUTTON_5_DOWN
                RawInputNative.BUTTON_5_UP
                flags

        if middleHeldChanged || mouse4HeldChanged || mouse5HeldChanged then
            InputAccumulator.set_pivot_held (heldPivotButtons <> 0) input

        if middlePanHeldChanged || mouse4PanHeldChanged || mouse5PanHeldChanged then
            InputAccumulator.set_pan_held (heldPanButtons <> 0) input

        let pivotToggleRequested =
            middlePivotRequested || mouse4PivotRequested || mouse5PivotRequested

        if pivotToggleRequested then
            InputAccumulator.request_pivot_toggle input

        let panToggleRequested =
            middlePanRequested || mouse4PanRequested || mouse5PanRequested

        if panToggleRequested then
            InputAccumulator.request_pan_toggle input

        let mutable retargetRequested = false

        if middleDown && middleRetargetMode <> int RetargetMode.Off then
            InputAccumulator.request_retarget (enum<RetargetMode> middleRetargetMode) input
            retargetRequested <- true

        if mouse4Down && mouse4RetargetMode <> int RetargetMode.Off then
            InputAccumulator.request_retarget (enum<RetargetMode> mouse4RetargetMode) input
            retargetRequested <- true

        if mouse5Down && mouse5RetargetMode <> int RetargetMode.Off then
            InputAccumulator.request_retarget (enum<RetargetMode> mouse5RetargetMode) input
            retargetRequested <- true

        let heldEntryReleased =
            exitsWhenRightMouseReleased && flags &&& RawInputNative.RIGHT_BUTTON_UP <> 0us

        let leftExitRequested =
            config.exit_on_mouse_left && flags &&& RawInputNative.LEFT_BUTTON_UP <> 0us

        let rightExitRequested =
            not exitsWhenRightMouseReleased
            && config.exit_on_mouse_right
            && flags &&& RawInputNative.RIGHT_BUTTON_UP <> 0us

        let exitReason =
            if heldEntryReleased then
                Some RightMouseReleased
            elif leftExitRequested || rightExitRequested then
                Some ExplicitKeepCamera
            else
                None

        match exitReason with
        | Some reason -> InputAccumulator.request_exit reason input
        | None -> ()

        mouseMoved
        || wheelDelta <> 0
        || middleHeldChanged
        || mouse4HeldChanged
        || mouse5HeldChanged
        || middlePanHeldChanged
        || mouse4PanHeldChanged
        || mouse5PanHeldChanged
        || pivotToggleRequested
        || panToggleRequested
        || retargetRequested
        || rawButtonEventAdded
        || Option.isSome exitReason

    member _.SignalInputAvailable() = inputAvailable.Invoke()

    member _.FailRuntime(error: exn) =
        Debug.WriteLine $"RhinosCanFly raw-input receiver failed: {error.Message}"
        runtimeFailed.Invoke error
