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

    let registrationRetryTimer =
        new Timer(Interval = RawInputNative.REGISTRATION_RETRY_INTERVAL_MS)

    let mutable handleCreated = false
    let mutable bufferFreed = false
    let mutable registrationLease: RawInputNative.MouseRegistrationLease option = None
    let mutable startupError: exn option = None
    let mutable registrationReleaseError: string option = None
    let mutable stopRequested = false
    let mutable registrationRetryTimerDisposed = false

    [<Literal>]
    let MOUSE4_PIVOT_BIT = 1

    [<Literal>]
    let MOUSE5_PIVOT_BIT = 2

    let initial_held_pivot_bit (action: MouseGestureAction) (virtualKey: int) (buttonBit: int) =
        if
            action = MouseGestureAction.HoldPivot
            && Win32Native.GetAsyncKeyState virtualKey < 0s
        then
            buttonBit
        else
            0

    let current_held_pivot_buttons () =
        let mouse4 =
            initial_held_pivot_bit config.mouse4_action Win32Native.VK_XBUTTON1 MOUSE4_PIVOT_BIT

        let mouse5 =
            initial_held_pivot_bit config.mouse5_action Win32Native.VK_XBUTTON2 MOUSE5_PIVOT_BIT

        mouse4 ||| mouse5

    let mutable heldPivotButtons = 0

    let update_held_pivot
        (action: MouseGestureAction)
        (buttonBit: int)
        (downFlag: uint16)
        (upFlag: uint16)
        (flags: uint16)
        =
        if action = MouseGestureAction.HoldPivot then
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
            | Ok RawInputNative.OwnRegistrationRemovedButPreviousRegistrationLost ->
                registrationReleaseError <-
                    Some
                        "RhinosCanFly removed its raw-mouse registration but could not restore the previous registration. Restart Rhino before flying again."

                finish_message_loop ()
                Ok()
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

        match registrationReleaseError with
        | Some error -> errors.Add(InvalidOperationException error)
        | None -> ()

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
            mouse.flags &&& RawInputNative.MOUSE_MOVE_ABSOLUTE = 0us
            && (mouse.last_x <> 0 || mouse.last_y <> 0)

        if mouseMoved then
            InputAccumulator.add_mouse mouse.last_x mouse.last_y input

        let flags = RawInputNative.button_flags mouse

        let wheelDelta =
            if flags &&& RawInputNative.MOUSE_WHEEL <> 0us then
                RawInputNative.signed_button_data mouse
            else
                0

        if wheelDelta <> 0 then
            InputAccumulator.add_wheel wheelDelta input

        let middlePivotRequested =
            config.middle_mouse_while_flying = FlyingMiddleMouseMode.TogglePivot
            && flags &&& RawInputNative.MIDDLE_BUTTON_DOWN <> 0us

        let mouse4PivotRequested =
            config.mouse4_action = MouseGestureAction.TogglePivot
            && flags &&& RawInputNative.BUTTON_4_DOWN <> 0us

        let mouse5PivotRequested =
            config.mouse5_action = MouseGestureAction.TogglePivot
            && flags &&& RawInputNative.BUTTON_5_DOWN <> 0us

        let mouse4HeldChanged =
            update_held_pivot
                config.mouse4_action
                MOUSE4_PIVOT_BIT
                RawInputNative.BUTTON_4_DOWN
                RawInputNative.BUTTON_4_UP
                flags

        let mouse5HeldChanged =
            update_held_pivot
                config.mouse5_action
                MOUSE5_PIVOT_BIT
                RawInputNative.BUTTON_5_DOWN
                RawInputNative.BUTTON_5_UP
                flags

        if mouse4HeldChanged || mouse5HeldChanged then
            InputAccumulator.set_pivot_held (heldPivotButtons <> 0) input

        let pivotToggleRequested =
            middlePivotRequested || mouse4PivotRequested || mouse5PivotRequested

        if pivotToggleRequested then
            InputAccumulator.request_pivot_toggle input

        let heldEntryReleased =
            sessionMode.lifetime = FlightLifetime.WhileRightMouseHeld
            && flags &&& RawInputNative.RIGHT_BUTTON_UP <> 0us

        let leftExitRequested =
            config.exit_on_mouse_left && flags &&& RawInputNative.LEFT_BUTTON_UP <> 0us

        let rightExitRequested =
            sessionMode.lifetime = FlightLifetime.UntilExit
            && config.exit_on_mouse_right
            && flags &&& RawInputNative.RIGHT_BUTTON_UP <> 0us

        let middleExitRequested =
            config.middle_mouse_while_flying = FlyingMiddleMouseMode.ExitFlight
            && flags &&& RawInputNative.MIDDLE_BUTTON_UP <> 0us

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
            parameters.Parent <- nativeint RawInputNative.MESSAGE_ONLY_WINDOW
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
        let mutable baseAttempted = false

        try
            try
                if message.Msg = RawInputNative.STOP_MESSAGE then
                    message.Result <- nativeint 0
                    request_stop ()
                elif message.Msg = RawInputNative.MESSAGE then
                    let mutable mouse = Unchecked.defaultof<RawInputNative.Mouse>

                    if
                        not stopRequested
                        && RawInputNative.try_read_mouse message.LParam buffer bufferCapacity &mouse
                    then
                        process_mouse mouse

                    baseAttempted <- true
                    base.WndProc(&message)
                else
                    baseAttempted <- true
                    base.WndProc(&message)
            with error ->
                fail_runtime error
                message.Result <- nativeint 0
        finally
            if message.Msg = RawInputNative.MESSAGE && not baseAttempted then
                try
                    baseAttempted <- true
                    base.WndProc(&message)
                with cleanupError ->
                    Debug.WriteLine $"RhinosCanFly raw-input message cleanup failed: {cleanupError}"
