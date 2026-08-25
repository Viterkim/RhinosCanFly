namespace RhinosCanFly.Platform.Win

open System
open System.Diagnostics
open RhinosCanFly

module RawInputSessionEvents =
    let add_button
        (event: RawMouseButtonEvent)
        (timestamp: int64)
        (modifiers: MouseModifiers)
        (input: InputAccumulator.State)
        (buttonObserved: Action<RawMouseButtonTransition>)
        =
        let transition =
            { event = event
              timestamp = timestamp
              modifiers = modifiers }

        buttonObserved.Invoke transition
        InputAccumulator.add_raw_mouse_button_transition transition input

type RawInputSession
    (
        input: InputAccumulator.State,
        inputAvailable: Action,
        buttonObserved: Action<RawMouseButtonTransition>,
        runtimeFailed: Action<exn>
    ) =

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

    member _.ProcessMouse(mouse: RawInputNative.Mouse) =
        let flags = RawInputNative.button_flags mouse

        let buttonTimestamp =
            if flags &&& rawMouseButtonTransitionFlags <> 0us then
                Stopwatch.GetTimestamp()
            else
                0L

        let modifiers =
            if buttonTimestamp = 0L then
                MouseModifiers.none
            else
                Win32.mouse_modifiers ()

        let mutable buttonAdded = false

        // A press owns this packet's movement and a release ends it afterwards.
        if flags &&& RawInputNative.LEFT_BUTTON_DOWN <> 0us then
            RawInputSessionEvents.add_button RawMouseButtonEvent.LeftDown buttonTimestamp modifiers input buttonObserved
            buttonAdded <- true

        if flags &&& RawInputNative.RIGHT_BUTTON_DOWN <> 0us then
            RawInputSessionEvents.add_button
                RawMouseButtonEvent.RightDown
                buttonTimestamp
                modifiers
                input
                buttonObserved

            buttonAdded <- true

        if flags &&& RawInputNative.MIDDLE_BUTTON_DOWN <> 0us then
            RawInputSessionEvents.add_button
                RawMouseButtonEvent.MiddleDown
                buttonTimestamp
                modifiers
                input
                buttonObserved

            buttonAdded <- true

        if flags &&& RawInputNative.BUTTON_4_DOWN <> 0us then
            RawInputSessionEvents.add_button
                RawMouseButtonEvent.Mouse4Down
                buttonTimestamp
                modifiers
                input
                buttonObserved

            buttonAdded <- true

        if flags &&& RawInputNative.BUTTON_5_DOWN <> 0us then
            RawInputSessionEvents.add_button
                RawMouseButtonEvent.Mouse5Down
                buttonTimestamp
                modifiers
                input
                buttonObserved

            buttonAdded <- true

        let mouseMoved =
            mouse.flags &&& RawInputNative.MOUSE_MOVE_ABSOLUTE = 0us
            && (mouse.last_x <> 0 || mouse.last_y <> 0)

        if mouseMoved then
            InputAccumulator.add_mouse mouse.last_x mouse.last_y input

        let wheelDelta =
            if flags &&& RawInputNative.MOUSE_WHEEL <> 0us then
                RawInputNative.signed_button_data mouse
            else
                0

        if wheelDelta <> 0 then
            InputAccumulator.add_wheel wheelDelta input

        if flags &&& RawInputNative.LEFT_BUTTON_UP <> 0us then
            RawInputSessionEvents.add_button RawMouseButtonEvent.LeftUp buttonTimestamp modifiers input buttonObserved
            buttonAdded <- true

        if flags &&& RawInputNative.RIGHT_BUTTON_UP <> 0us then
            RawInputSessionEvents.add_button RawMouseButtonEvent.RightUp buttonTimestamp modifiers input buttonObserved
            buttonAdded <- true

        if flags &&& RawInputNative.MIDDLE_BUTTON_UP <> 0us then
            RawInputSessionEvents.add_button RawMouseButtonEvent.MiddleUp buttonTimestamp modifiers input buttonObserved
            buttonAdded <- true

        if flags &&& RawInputNative.BUTTON_4_UP <> 0us then
            RawInputSessionEvents.add_button RawMouseButtonEvent.Mouse4Up buttonTimestamp modifiers input buttonObserved
            buttonAdded <- true

        if flags &&& RawInputNative.BUTTON_5_UP <> 0us then
            RawInputSessionEvents.add_button RawMouseButtonEvent.Mouse5Up buttonTimestamp modifiers input buttonObserved
            buttonAdded <- true

        mouseMoved || wheelDelta <> 0 || buttonAdded

    member _.SignalInputAvailable() = inputAvailable.Invoke()

    member _.FailRuntime(error: exn) =
        Debug.WriteLine $"RhinosCanFly raw-input receiver failed: {error.Message}"
        runtimeFailed.Invoke error
