namespace RhinosCanFly.Platform.Win

open System
open System.Diagnostics
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

    let inputBuffer =
        new RawInputNative.InputBuffer(RawInputNative.initial_input_buffer_capacity)

    let registrationRetryTimer =
        new Timer(Interval = RawInputNative.REGISTRATION_RETRY_INTERVAL_MS)

    let mutable handleCreated = false
    let mutable inputBufferDisposed = false
    let mutable registrationLease: RawInputNative.MouseRegistrationLease option = None
    let mutable startupError: exn option = None
    let mutable registrationReleaseError: string option = None
    let mutable stopRequested = false
    let mutable registrationRetryTimerDisposed = false

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
    let middleRetargetMode = int (RoutedMouseAction.retarget_mode config.middle_mouse_action)
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
        let middle =
            initial_held_bit middleHoldPan Win32Native.VK_MBUTTON MIDDLE_BUTTON_BIT

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
            if not inputBufferDisposed then
                inputBuffer.Dispose()
                inputBufferDisposed <- true
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

        let panToggleRequested = middlePanRequested || mouse4PanRequested || mouse5PanRequested

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
            sessionMode.lifetime = FlightLifetime.WhileRightMouseHeld
            && flags &&& RawInputNative.RIGHT_BUTTON_UP <> 0us

        let leftExitRequested =
            config.exit_on_mouse_left && flags &&& RawInputNative.LEFT_BUTTON_UP <> 0us

        let rightExitRequested =
            sessionMode.lifetime = FlightLifetime.UntilExit
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

    let grow_input_buffer (minimumCapacity: uint32) =
        let currentCapacity = int64 inputBuffer.Capacity

        let maximumCapacity =
            int64 Int32.MaxValue - int64 (RawInputNative.RAW_INPUT_ALIGNMENT - 1)

        let doubledCapacity = min maximumCapacity (currentCapacity * 2L)
        let requiredCapacity = max (int64 minimumCapacity) doubledCapacity

        if requiredCapacity <= currentCapacity || requiredCapacity > maximumCapacity then
            invalidOp $"The raw-input buffer cannot grow to {minimumCapacity} bytes."

        inputBuffer.EnsureCapacity(int requiredCapacity)

    let process_current_input (rawInput: nativeint) =
        let mutable reading = true
        let mutable workAdded = false

        while reading do
            let mutable requiredBytes = 0u
            let mutable errorCode = 0
            let mutable mouse = Unchecked.defaultof<RawInputNative.Mouse>

            let result =
                RawInputNative.read_current_mouse rawInput inputBuffer &requiredBytes &errorCode &mouse

            match result with
            | RawInputNative.MouseReadResult.Mouse ->
                workAdded <- process_mouse mouse
                reading <- false
            | RawInputNative.MouseReadResult.Ignored -> reading <- false
            | RawInputNative.MouseReadResult.BufferTooSmall -> grow_input_buffer requiredBytes
            | RawInputNative.MouseReadResult.Failed -> Win32.win32_error "GetRawInputData" errorCode |> invalidOp
            | RawInputNative.MouseReadResult.Malformed -> invalidOp "GetRawInputData returned malformed mouse input."
            | _ -> invalidOp "GetRawInputData returned an unknown result."

        workAdded

    let process_buffered_input () =
        let mutable draining = true
        let mutable workAdded = false

        while draining do
            let mutable bufferBytes = 0u
            let mutable errorCode = 0
            let count = RawInputNative.read_buffered inputBuffer &bufferBytes &errorCode

            if count = 0u then
                draining <- false
            elif count = UInt32.MaxValue then
                if errorCode = RawInputNative.ERROR_INSUFFICIENT_BUFFER then
                    grow_input_buffer bufferBytes
                else
                    Win32.win32_error "GetRawInputBuffer" errorCode |> invalidOp
            else
                let mutable index = 0u
                let mutable offset = 0

                // Keep this path on structs, ints and the reused buffer.
                while index < count do
                    if offset < 0 || offset > inputBuffer.Capacity - int RawInputNative.headerSize then
                        invalidOp "GetRawInputBuffer returned a record outside its buffer."

                    let record = IntPtr.Add(inputBuffer.Pointer, offset)
                    let availableBytes = inputBuffer.Capacity - offset
                    let mutable recordSize = 0u
                    let mutable mouse = Unchecked.defaultof<RawInputNative.Mouse>

                    let result = RawInputNative.decode_mouse record availableBytes &recordSize &mouse

                    match result with
                    | RawInputNative.MouseReadResult.Mouse ->
                        if process_mouse mouse then
                            workAdded <- true
                    | RawInputNative.MouseReadResult.Ignored -> ()
                    | RawInputNative.MouseReadResult.Malformed ->
                        invalidOp "GetRawInputBuffer returned malformed input."
                    | RawInputNative.MouseReadResult.Failed
                    | RawInputNative.MouseReadResult.BufferTooSmall
                    | _ -> invalidOp "GetRawInputBuffer returned an unknown decode result."

                    let step = RawInputNative.aligned_record_size recordSize

                    if step <= 0 || step > availableBytes then
                        invalidOp "GetRawInputBuffer returned an invalid record size."

                    offset <- offset + step
                    index <- index + 1u

        workAdded

    let process_input_message (rawInput: nativeint) =
        let currentAdded = process_current_input rawInput
        let bufferedAdded = process_buffered_input ()

        if currentAdded || bufferedAdded then
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
                heldPanButtons <- current_held_pan_buttons ()
                InputAccumulator.set_pivot_held (heldPivotButtons <> 0) input
                InputAccumulator.set_pan_held (heldPanButtons <> 0) input
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
                    if not stopRequested then
                        process_input_message message.LParam

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
