namespace RhinosCanFly.Platform.Win

open System
open System.Diagnostics
open System.Runtime.InteropServices
open System.Windows.Forms
open RhinosCanFly

type RawInputReceiver
    (config: FlyConfig, sessionMode: FlightSessionMode, input: InputAccumulator.State, inputAvailable: Action) as self =
    inherit NativeWindow()

    let bufferCapacity =
        Marshal.SizeOf<RawInputNative.Header>() + Marshal.SizeOf<RawInputNative.Mouse>()

    let buffer = Marshal.AllocHGlobal bufferCapacity
    let mutable handleCreated = false
    let mutable bufferFreed = false
    let mutable rawRegistered = false
    let mutable registrationRestored = false
    let mutable previousMouse: RawInputNative.Device option = None
    let mutable heldPivotButtons = 0

    [<Literal>]
    let mouse4PivotBit = 1

    [<Literal>]
    let mouse5PivotBit = 2

    let update_held_pivot
        (enabled: bool)
        (mode: MouseButtonPivotMode)
        (buttonBit: int)
        (downFlag: uint16)
        (upFlag: uint16)
        (flags: uint16)
        =
        if enabled && mode = MouseButtonPivotMode.Hold then
            if flags &&& downFlag <> 0us then
                heldPivotButtons <- heldPivotButtons ||| buttonBit

            if flags &&& upFlag <> 0us then
                heldPivotButtons <- heldPivotButtons &&& (~~~buttonBit)

    let restore_registration () =
        if rawRegistered && not registrationRestored then
            match RawInputNative.restore_mouse previousMouse with
            | Ok() -> registrationRestored <- true
            | Error restoreError ->
                match previousMouse with
                | Some _ ->
                    match RawInputNative.unregister_mouse () with
                    | Ok() ->
                        raise (
                            InvalidOperationException(
                                $"{restoreError}; the previous raw-mouse registration could not be restored"
                            )
                        )
                    | Error removalError -> raise (InvalidOperationException($"{restoreError}; {removalError}"))
                | None -> raise (InvalidOperationException restoreError)

    let release_resources () =
        let errors = ResizeArray<exn>()

        try
            restore_registration ()
        with error ->
            errors.Add error

        try
            if handleCreated then
                self.DestroyHandle()
                handleCreated <- false
        with error ->
            errors.Add error

        try
            if not bufferFreed then
                Marshal.FreeHGlobal buffer
                bufferFreed <- true
        with error ->
            errors.Add error

        match errors.Count with
        | 0 -> ()
        | 1 -> raise errors[0]
        | _ -> raise (AggregateException errors)

    let process_mouse (mouse: RawInputNative.Mouse) =
        if mouse.flags &&& RawInputNative.mouse_move_absolute = 0us then
            InputAccumulator.add_mouse mouse.last_x mouse.last_y input

        let flags = RawInputNative.button_flags mouse

        if flags &&& RawInputNative.mouse_wheel <> 0us then
            InputAccumulator.add_wheel (RawInputNative.signed_button_data mouse) input

        let middlePivotRequested =
            config.middle_mouse_while_flying = FlyingMiddleMouseMode.TogglePivot
            && flags &&& RawInputNative.middle_button_down <> 0us

        let mouse4PivotRequested =
            config.mouse4_also_while_flying
            && config.mouse4_pivot_mode = MouseButtonPivotMode.Toggle
            && flags &&& RawInputNative.button_4_down <> 0us

        let mouse5PivotRequested =
            config.mouse5_also_while_flying
            && config.mouse5_pivot_mode = MouseButtonPivotMode.Toggle
            && flags &&& RawInputNative.button_5_down <> 0us

        update_held_pivot
            config.mouse4_also_while_flying
            config.mouse4_pivot_mode
            mouse4PivotBit
            RawInputNative.button_4_down
            RawInputNative.button_4_up
            flags

        update_held_pivot
            config.mouse5_also_while_flying
            config.mouse5_pivot_mode
            mouse5PivotBit
            RawInputNative.button_5_down
            RawInputNative.button_5_up
            flags

        InputAccumulator.set_pivot_held (heldPivotButtons <> 0) input

        if middlePivotRequested || mouse4PivotRequested || mouse5PivotRequested then
            InputAccumulator.request_pivot_toggle input

        if
            sessionMode = FlightSessionMode.WhileRightMouseHeld
            && flags &&& RawInputNative.right_button_up <> 0us
            || config.exit_on_mouse_left && flags &&& RawInputNative.left_button_down <> 0us
            || sessionMode = FlightSessionMode.Persistent
               && config.exit_on_mouse_right
               && flags &&& RawInputNative.right_button_down <> 0us
            || config.middle_mouse_while_flying = FlyingMiddleMouseMode.ExitFlying
               && flags &&& RawInputNative.middle_button_up <> 0us
        then
            InputAccumulator.request_exit input

        inputAvailable.Invoke()

    do
        try
            let parameters = CreateParams()
            parameters.Caption <- "RhinosCanFly raw input"
            parameters.Parent <- nativeint RawInputNative.message_only_window
            self.CreateHandle parameters
            handleCreated <- true

            match RawInputNative.get_registered_mouse () with
            | Error error -> failwith error
            | Ok previous ->
                previousMouse <- previous

                match RawInputNative.register_mouse self.Handle with
                | Ok() -> rawRegistered <- true
                | Error error -> failwith error
        with error ->
            try
                release_resources ()
            with cleanupError ->
                raise (AggregateException(error, cleanupError))

            raise error

    member _.WindowHandle = self.Handle

    member _.ReleaseResources() = release_resources ()

    override _.WndProc(message: byref<Message>) =
        if message.Msg = RawInputNative.stop_message then
            message.Result <- nativeint 0
            Application.ExitThread()
        elif message.Msg = RawInputNative.message then
            let mutable mouse = Unchecked.defaultof<RawInputNative.Mouse>

            if RawInputNative.try_read_mouse message.LParam buffer bufferCapacity &mouse then
                process_mouse mouse

            base.WndProc(&message)
        else
            base.WndProc(&message)
