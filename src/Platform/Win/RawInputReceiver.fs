namespace RhinosCanFly.Platform.Win

open System
open System.Diagnostics
open System.Windows.Forms
open RhinosCanFly

type RawInputReceiver(processControl: Action) as self =
    inherit NativeWindow()

    let inputBuffer =
        new RawInputNative.InputBuffer(RawInputNative.initial_input_buffer_capacity)

    let registrationRetryTimer =
        new Timer(Interval = RawInputNative.REGISTRATION_RETRY_INTERVAL_MS)

    let mutable handleCreated = false
    let mutable inputBufferDisposed = false
    let mutable registrationRetryTimerDisposed = false
    let mutable activeSession: RawInputSession option = None
    let mutable registrationLease: RawInputNative.MouseRegistrationLease option = None
    let mutable registrationReleaseError: string option = None
    let mutable sessionFinished: Action option = None
    let mutable stopRequested = false
    let mutable sessionFinishedNotified = false

    let registration_relinquished () =
        match registrationLease with
        | None -> true
        | Some lease -> lease.relinquished

    let finish_session () =
        registrationRetryTimer.Stop()

        if not sessionFinishedNotified then
            sessionFinishedNotified <- true

            match sessionFinished with
            | Some finished -> finished.Invoke()
            | None -> ()

    let try_release_registration () =
        match registrationLease with
        | None ->
            finish_session ()
            Ok()
        | Some lease ->
            match RawInputNative.release_mouse_registration lease with
            | Ok RawInputNative.OwnRegistrationRemovedButPreviousRegistrationLost ->
                registrationReleaseError <-
                    Some
                        "RhinosCanFly removed its raw-mouse registration but could not restore the previous registration. Restart Rhino before flying again."

                finish_session ()
                Ok()
            | Ok _ ->
                finish_session ()
                Ok()
            | Error error ->
                if not registrationRetryTimer.Enabled then
                    registrationRetryTimer.Start()

                Error error

    let request_stop () =
        stopRequested <- true
        try_release_registration () |> ignore

    let release_session () =
        if not (registration_relinquished ()) then
            invalidOp "The raw-input session cannot be released while mouse registration still belongs to it."

        let releaseError = registrationReleaseError
        activeSession <- None
        registrationLease <- None
        registrationReleaseError <- None
        sessionFinished <- None
        stopRequested <- false
        sessionFinishedNotified <- false

        match releaseError with
        | Some error -> raise (InvalidOperationException error)
        | None -> ()

    let release_resources () =
        if Option.isSome activeSession || Option.isSome sessionFinished then
            invalidOp "The raw-input worker cannot release its window while a session is active."

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

    let grow_input_buffer (minimumCapacity: uint32) =
        let currentCapacity = int64 inputBuffer.Capacity

        let maximumCapacity =
            int64 Int32.MaxValue - int64 (RawInputNative.RAW_INPUT_ALIGNMENT - 1)

        let doubledCapacity = min maximumCapacity (currentCapacity * 2L)
        let requiredCapacity = max (int64 minimumCapacity) doubledCapacity

        if requiredCapacity <= currentCapacity || requiredCapacity > maximumCapacity then
            invalidOp $"The raw-input buffer cannot grow to {minimumCapacity} bytes."

        inputBuffer.EnsureCapacity(int requiredCapacity)

    let process_current_input (session: RawInputSession) (rawInput: nativeint) =
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
                workAdded <- session.ProcessMouse mouse
                reading <- false
            | RawInputNative.MouseReadResult.Ignored -> reading <- false
            | RawInputNative.MouseReadResult.BufferTooSmall -> grow_input_buffer requiredBytes
            | RawInputNative.MouseReadResult.Failed -> Win32.win32_error "GetRawInputData" errorCode |> invalidOp
            | RawInputNative.MouseReadResult.Malformed -> invalidOp "GetRawInputData returned malformed mouse input."
            | _ -> invalidOp "GetRawInputData returned an unknown result."

        workAdded

    let process_buffered_input (session: RawInputSession) =
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
                        if session.ProcessMouse mouse then
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

    let discard_buffered_input () =
        let mutable draining = true

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
                    Win32.win32_error "GetRawInputBuffer(discard)" errorCode |> invalidOp

    let process_input_message (rawInput: nativeint) =
        match activeSession with
        | Some session ->
            let currentAdded = process_current_input session rawInput
            let bufferedAdded = process_buffered_input session

            if currentAdded || bufferedAdded then
                session.SignalInputAvailable()
        | None -> ()

    let fail_runtime (error: exn) =
        match activeSession with
        | Some session -> session.FailRuntime error
        | None -> Debug.WriteLine $"RhinosCanFly idle raw-input receiver failed: {error.Message}"

        request_stop ()

    do
        registrationRetryTimer.Tick.Add(fun (_event: EventArgs) ->
            if stopRequested then
                try_release_registration () |> ignore)

        let parameters = CreateParams()
        parameters.Caption <- "RhinosCanFly raw input"
        parameters.Parent <- nativeint RawInputNative.MESSAGE_ONLY_WINDOW
        self.CreateHandle parameters
        handleCreated <- true

    member _.WindowHandle = self.Handle

    member _.RegistrationRelinquished = registration_relinquished ()

    member _.StartSession
        (
            config: RawMouseInputConfig,
            sessionMode: FlightSessionMode,
            input: InputAccumulator.State,
            inputAvailable: Action,
            buttonObserved: Action<RawMouseButtonTransition>,
            registrationReady: Action<RawInputNative.MouseRegistrationLease>,
            runtimeFailed: Action<exn>,
            finished: Action
        ) : exn option =
        if Option.isSome activeSession || Option.isSome sessionFinished then
            invalidOp "Another raw-input session is already active."

        stopRequested <- false
        sessionFinishedNotified <- false
        registrationReleaseError <- None
        sessionFinished <- Some finished

        try
            discard_buffered_input ()

            let session =
                RawInputSession(config, sessionMode, input, inputAvailable, buttonObserved, runtimeFailed)

            activeSession <- Some session

            match RawInputNative.acquire_mouse_registration self.Handle with
            | RawInputNative.Acquired lease ->
                registrationLease <- Some lease
                registrationReady.Invoke lease
                session.InitializeHeldButtons()
                None
            | RawInputNative.Failed error -> Some(InvalidOperationException error)
            | RawInputNative.CleanupPending(error, lease) ->
                registrationLease <- Some lease
                registrationReady.Invoke lease
                Some(InvalidOperationException error)
        with error ->
            Some error

    member _.RequestStop() = request_stop ()

    member _.ReleaseSession() = release_session ()

    member _.ReleaseResources() = release_resources ()

    override _.WndProc(message: byref<Message>) =
        let mutable baseAttempted = false

        try
            try
                if message.Msg = RawInputNative.CONTROL_MESSAGE then
                    message.Result <- nativeint 0
                    processControl.Invoke()
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
