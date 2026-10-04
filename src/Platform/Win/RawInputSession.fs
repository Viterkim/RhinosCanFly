namespace RhinosCanFly.Platform.Win

open System
open System.Diagnostics
open System.Threading
open RhinosCanFly

module RawMouseButtons =
    // These pairs survive the raw session until their actual release is consumed.
    let gate = obj ()
    let mutable pending = 0
    let mutable tracked = 0
    let mutable handoff = false

    let any () = Volatile.Read(&pending) <> 0

    let begin_handoff (owned: int) =
        lock gate (fun () ->
            pending <- pending ||| owned
            tracked <- pending
            handoff <- true)

    let end_raw_delivery () = lock gate (fun () -> handoff <- false)

    let finish_handoff () =
        lock gate (fun () ->
            handoff <- false
            let released = tracked &&& ~~~pending
            tracked <- 0
            released)

    let legacy_transition (key: int) (released: bool) =
        let bit = 1 <<< key

        lock gate (fun () ->
            if handoff then
                let owned = tracked &&& bit <> 0

                if not released || owned then
                    tracked <- tracked ||| bit

                    pending <- if released then pending &&& ~~~bit else pending ||| bit

                struct (not released || owned, false)
            else
                let owned = (pending ||| tracked) &&& bit <> 0
                pending <- pending &&& ~~~bit
                tracked <- tracked &&& ~~~bit
                struct (released && owned, owned))

    let observe (event: RawMouseButtonEvent) =
        let struct (key, down) =
            match event with
            | RawMouseButtonEvent.LeftDown -> struct (Win32Native.VK_LBUTTON, true)
            | RawMouseButtonEvent.LeftUp -> struct (Win32Native.VK_LBUTTON, false)
            | RawMouseButtonEvent.RightDown -> struct (Win32Native.VK_RBUTTON, true)
            | RawMouseButtonEvent.RightUp -> struct (Win32Native.VK_RBUTTON, false)
            | RawMouseButtonEvent.MiddleDown -> struct (Win32Native.VK_MBUTTON, true)
            | RawMouseButtonEvent.MiddleUp -> struct (Win32Native.VK_MBUTTON, false)
            | RawMouseButtonEvent.Mouse4Down -> struct (Win32Native.VK_XBUTTON1, true)
            | RawMouseButtonEvent.Mouse4Up -> struct (Win32Native.VK_XBUTTON1, false)
            | RawMouseButtonEvent.Mouse5Down -> struct (Win32Native.VK_XBUTTON2, true)
            | RawMouseButtonEvent.Mouse5Up -> struct (Win32Native.VK_XBUTTON2, false)
            | _ -> struct (0, false)

        if key <> 0 then
            lock gate (fun () ->
                let bit = 1 <<< key
                tracked <- tracked ||| bit
                pending <- if down then pending ||| bit else pending &&& ~~~bit)

module RawInputSessionEvents =
    let add_button (event: RawMouseButtonEvent) (modifiers: MouseModifiers) (input: InputAccumulator.State) =
        RawMouseButtons.observe event
        let transition = { event = event; modifiers = modifiers }

        InputAccumulator.add_raw_mouse_button_transition transition input

type RawInputSession
    (buttons_swapped: bool, input: InputAccumulator.State, input_available: Action, runtime_failed: Action<exn>) =

    // Keep Down/Up identity fixed for this raw session.
    let struct (left_down, left_up, right_down, right_up) =
        if buttons_swapped then
            struct (RawMouseButtonEvent.RightDown,
                    RawMouseButtonEvent.RightUp,
                    RawMouseButtonEvent.LeftDown,
                    RawMouseButtonEvent.LeftUp)
        else
            struct (RawMouseButtonEvent.LeftDown,
                    RawMouseButtonEvent.LeftUp,
                    RawMouseButtonEvent.RightDown,
                    RawMouseButtonEvent.RightUp)

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
            RawInputSessionEvents.add_button left_down modifiers input

            button_added <- true

        if flags &&& RawInputNative.RIGHT_BUTTON_DOWN <> 0us then
            RawInputSessionEvents.add_button right_down modifiers input

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

        let absolute_motion = mouse.flags &&& RawInputNative.MOUSE_MOVE_ABSOLUTE <> 0us

        let motion_warning =
            absolute_motion && InputAccumulator.observe_absolute_motion input

        let mouse_moved = not absolute_motion && (mouse.last_x <> 0 || mouse.last_y <> 0)

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
            RawInputSessionEvents.add_button left_up modifiers input

            button_added <- true

        if flags &&& RawInputNative.RIGHT_BUTTON_UP <> 0us then
            RawInputSessionEvents.add_button right_up modifiers input

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

        mouse_moved || wheel_delta <> 0 || button_added || motion_warning

    member _.SignalInputAvailable() = input_available.Invoke()

    member _.FailRuntime(error: exn) =
        Debug.WriteLine $"RhinosCanFly raw-input receiver failed: {error.Message}"
        runtime_failed.Invoke error
