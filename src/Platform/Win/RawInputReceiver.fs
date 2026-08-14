namespace RhinosCanFly.Platform.Win

open System
open System.Diagnostics
open System.Runtime.InteropServices
open System.Windows.Forms
open RhinosCanFly

type RawInputReceiver
    (
        config: RawInputConfig,
        sessionMode: FlightSessionMode,
        input: InputAccumulator.State,
        inputAvailable: Action,
        registrationReady: Action<RawInputNative.MouseRegistrationLease>,
        runtimeFailed: Action<exn>
    ) as self =
    inherit NativeWindow()

    let bufferCapacity = int RawInputNative.mouseInputSize
    let buffer = Marshal.AllocHGlobal bufferCapacity
    let registrationRetryTimer = new Timer(Interval = 250)
    let mutable handleCreated = false
    let mutable bufferFreed = false
    let mutable registrationLease: RawInputNative.MouseRegistrationLease option = None
    let mutable startupError: exn option = None
    let mutable stopRequested = false
    let mutable registrationRetryTimerDisposed = false

    [<Literal>]
    let mouse4PivotBit = 1

    [<Literal>]
    let mouse5PivotBit = 2

    let initial_held_pivot_bit (enabled: bool) (mode: MouseButtonPivotMode) (virtualKey: int) (buttonBit: int) =
        if
            enabled
            && mode = MouseButtonPivotMode.Hold
            && Win32Native.GetAsyncKeyState virtualKey < 0s
        then
            buttonBit
        else
            0

    let current_held_pivot_buttons () =
        let mouse4 =
            initial_held_pivot_bit
                config.mouse4_also_while_flying
                config.mouse4_pivot_mode
                Win32Native.VK_XBUTTON1
                mouse4PivotBit

        let mouse5 =
            initial_held_pivot_bit
                config.mouse5_also_while_flying
                config.mouse5_pivot_mode
                Win32Native.VK_XBUTTON2
                mouse5PivotBit

        mouse4 ||| mouse5

    let mutable heldPivotButtons = 0

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

            flags &&& (downFlag ||| upFlag) <> 0us
        else
            false

    let registration_relinquished () =
        match registrationLease with
        | None -> true
        | Some lease -> lease.relinquished

    let finish_message_loop () =
        registrationRetryTimer.Stop()
        Application.ExitThread()

    let try_release_registration () =
        match registrationLease with
        | None ->
            finish_message_loop ()
            Ok()
        | Some lease ->
            match RawInputNative.release_mouse_registration lease with
            | Ok _ ->
                finish_message_loop ()
                Ok()
            | Error error ->
                if not registrationRetryTimer.Enabled then
                    registrationRetryTimer.Start()

                Error error

    let request_stop () =
        stopRequested <- true
        try_release_registration () |> ignore

    let release_resources () =
        if not (registration_relinquished ()) then
            invalidOp "The raw-input worker cannot release its window while mouse registration still belongs to it."

        let errors = ResizeArray<exn>()

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

        try
            if not registrationRetryTimerDisposed then
                registrationRetryTimer.Dispose()
                registrationRetryTimerDisposed <- true
        with error ->
            errors.Add error

        match errors.Count with
        | 0 -> ()
        | 1 -> raise errors[0]
        | _ -> raise (AggregateException errors)

    let process_mouse (mouse: RawInputNative.Mouse) =
        let mouseMoved =
            mouse.flags &&& RawInputNative.mouse_move_absolute = 0us
            && (mouse.last_x <> 0 || mouse.last_y <> 0)

        if mouseMoved then
            InputAccumulator.add_mouse mouse.last_x mouse.last_y input

        let flags = RawInputNative.button_flags mouse

        let wheelDelta =
            if flags &&& RawInputNative.mouse_wheel <> 0us then
                RawInputNative.signed_button_data mouse
            else
                0

        if wheelDelta <> 0 then
            InputAccumulator.add_wheel wheelDelta input

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

        let mouse4HeldChanged =
            update_held_pivot
                config.mouse4_also_while_flying
                config.mouse4_pivot_mode
                mouse4PivotBit
                RawInputNative.button_4_down
                RawInputNative.button_4_up
                flags

        let mouse5HeldChanged =
            update_held_pivot
                config.mouse5_also_while_flying
                config.mouse5_pivot_mode
                mouse5PivotBit
                RawInputNative.button_5_down
                RawInputNative.button_5_up
                flags

        if mouse4HeldChanged || mouse5HeldChanged then
            InputAccumulator.set_pivot_held (heldPivotButtons <> 0) input

        let pivotToggleRequested =
            middlePivotRequested || mouse4PivotRequested || mouse5PivotRequested

        if pivotToggleRequested then
            InputAccumulator.request_pivot_toggle input

        let heldEntryReleased =
            sessionMode.lifetime = FlightLifetime.WhileRightMouseHeld
            && flags &&& RawInputNative.right_button_up <> 0us

        let leftExitRequested =
            config.exit_on_mouse_left && flags &&& RawInputNative.left_button_up <> 0us

        let rightExitRequested =
            sessionMode.lifetime = FlightLifetime.UntilExit
            && config.exit_on_mouse_right
            && flags &&& RawInputNative.right_button_up <> 0us

        let middleExitRequested =
            config.middle_mouse_while_flying = FlyingMiddleMouseMode.ExitFlying
            && flags &&& RawInputNative.middle_button_up <> 0us

        let exitReason =
            if heldEntryReleased then
                Some RightMouseReleased
            elif leftExitRequested || rightExitRequested || middleExitRequested then
                Some ExplicitKeepCamera
            else
                None

        match exitReason with
        | Some reason -> InputAccumulator.request_exit reason input
        | None -> ()

        if
            mouseMoved
            || wheelDelta <> 0
            || mouse4HeldChanged
            || mouse5HeldChanged
            || pivotToggleRequested
            || Option.isSome exitReason
        then
            inputAvailable.Invoke()

    let fail_runtime (error: exn) =
        Debug.WriteLine $"RhinosCanFly raw-input receiver failed: {error.Message}"
        runtimeFailed.Invoke error
        request_stop ()

    do
        registrationRetryTimer.Tick.Add(fun (_event: EventArgs) ->
            if stopRequested then
                try_release_registration () |> ignore)

        try
            let parameters = CreateParams()
            parameters.Caption <- "RhinosCanFly raw input"
            parameters.Parent <- nativeint RawInputNative.message_only_window
            self.CreateHandle parameters
            handleCreated <- true

            match RawInputNative.acquire_mouse_registration self.Handle with
            | RawInputNative.Acquired lease ->
                registrationLease <- Some lease
                registrationReady.Invoke lease
                heldPivotButtons <- current_held_pivot_buttons ()
                InputAccumulator.set_pivot_held (heldPivotButtons <> 0) input
            | RawInputNative.Failed error -> startupError <- Some(InvalidOperationException error)
            | RawInputNative.CleanupPending(error, lease) ->
                registrationLease <- Some lease
                registrationReady.Invoke lease
                startupError <- Some(InvalidOperationException error)
        with error ->
            startupError <- Some error

    member _.WindowHandle = self.Handle

    member _.StartupError = startupError

    member _.RegistrationRelinquished = registration_relinquished ()

    member _.RequestStop() = request_stop ()

    member _.ReleaseResources() = release_resources ()

    override _.WndProc(message: byref<Message>) =
        try
            if message.Msg = RawInputNative.stop_message then
                message.Result <- nativeint 0
                request_stop ()
            elif message.Msg = RawInputNative.message then
                let mutable mouse = Unchecked.defaultof<RawInputNative.Mouse>

                if
                    not stopRequested
                    && RawInputNative.try_read_mouse message.LParam buffer bufferCapacity &mouse
                then
                    process_mouse mouse

                base.WndProc(&message)
            else
                base.WndProc(&message)
        with error ->
            fail_runtime error
            message.Result <- nativeint 0
