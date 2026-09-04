namespace RhinosCanFly.Platform.Win

open System
open System.Diagnostics
open RhinosCanFly

module RawInputSessionEvents =
    let add_button (event: RawMouseButtonEvent) (modifiers: MouseModifiers) (input: InputAccumulator.State) =
        let transition = { event = event; modifiers = modifiers }

        InputAccumulator.add_raw_mouse_button_transition transition input

type RawInputSession(input: InputAccumulator.State, input_available: Action, runtime_failed: Action<exn>) =

    let raw_mouse_button_transition_flags =
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

        let has_button_transition = flags &&& raw_mouse_button_transition_flags <> 0us

        let modifiers =
            if has_button_transition then
                Win32.mouse_modifiers ()
            else
                MouseModifiers.none

        let mutable button_added = false

        // A press owns this packet's movement and a release ends it afterwards.
        if flags &&& RawInputNative.LEFT_BUTTON_DOWN <> 0us then
            RawInputSessionEvents.add_button RawMouseButtonEvent.LeftDown modifiers input
            button_added <- true

        if flags &&& RawInputNative.RIGHT_BUTTON_DOWN <> 0us then
            RawInputSessionEvents.add_button RawMouseButtonEvent.RightDown modifiers input

            button_added <- true

        if flags &&& RawInputNative.MIDDLE_BUTTON_DOWN <> 0us then
            RawInputSessionEvents.add_button RawMouseButtonEvent.MiddleDown modifiers input

            button_added <- true

        if flags &&& RawInputNative.BUTTON_4_DOWN <> 0us then
            RawInputSessionEvents.add_button RawMouseButtonEvent.Mouse4Down modifiers input

            button_added <- true

        if flags &&& RawInputNative.BUTTON_5_DOWN <> 0us then
            RawInputSessionEvents.add_button RawMouseButtonEvent.Mouse5Down modifiers input

            button_added <- true

        let mouse_moved =
            mouse.flags &&& RawInputNative.MOUSE_MOVE_ABSOLUTE = 0us
            && (mouse.last_x <> 0 || mouse.last_y <> 0)

        if mouse_moved then
            InputAccumulator.add_mouse mouse.last_x mouse.last_y input

        let wheel_delta =
            if flags &&& RawInputNative.MOUSE_WHEEL <> 0us then
                RawInputNative.signed_button_data mouse
            else
                0

        if wheel_delta <> 0 then
            InputAccumulator.add_wheel wheel_delta input

        if flags &&& RawInputNative.LEFT_BUTTON_UP <> 0us then
            RawInputSessionEvents.add_button RawMouseButtonEvent.LeftUp modifiers input
            button_added <- true

        if flags &&& RawInputNative.RIGHT_BUTTON_UP <> 0us then
            RawInputSessionEvents.add_button RawMouseButtonEvent.RightUp modifiers input
            button_added <- true

        if flags &&& RawInputNative.MIDDLE_BUTTON_UP <> 0us then
            RawInputSessionEvents.add_button RawMouseButtonEvent.MiddleUp modifiers input
            button_added <- true

        if flags &&& RawInputNative.BUTTON_4_UP <> 0us then
            RawInputSessionEvents.add_button RawMouseButtonEvent.Mouse4Up modifiers input
            button_added <- true

        if flags &&& RawInputNative.BUTTON_5_UP <> 0us then
            RawInputSessionEvents.add_button RawMouseButtonEvent.Mouse5Up modifiers input
            button_added <- true

        mouse_moved || wheel_delta <> 0 || button_added

    member _.SignalInputAvailable() = input_available.Invoke()

    member _.FailRuntime(error: exn) =
        Debug.WriteLine $"RhinosCanFly raw-input receiver failed: {error.Message}"
        runtime_failed.Invoke error
