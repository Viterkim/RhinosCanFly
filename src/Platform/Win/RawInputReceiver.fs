namespace RhinosCanFly.Platform.Win

open System
open System.Diagnostics
open System.Windows.Forms
open RhinosCanFly

type RawInputReceiver(process_control: Action) as self =
    inherit NativeWindow()

    let input_buffer =
        new RawInputNative.InputBuffer(RawInputNative.initial_input_buffer_capacity)

    let registration_retry_timer =
        new Timer(Interval = RawInputNative.REGISTRATION_RETRY_INTERVAL_MS)

    let mutable handle_created = false
    let mutable input_buffer_disposed = false
    let mutable registration_retry_timer_disposed = false
    let mutable active_session: RawInputSession option = None
    let mutable registration_lease: RawInputNative.MouseRegistrationLease option = None
    let mutable registration_release_error: string option = None
    let mutable session_finished: Action option = None
    let mutable stop_requested = false
    let mutable session_finished_notified = false

    let registration_relinquished () =
        match registration_lease with
        | None -> true
        | Some lease -> lease.relinquished

    let finish_session () =
        registration_retry_timer.Stop()

        if not session_finished_notified then
            session_finished_notified <- true

            match session_finished with
            | Some finished -> finished.Invoke()
            | None -> ()

    let try_release_registration () =
        match registration_lease with
        | None ->
            finish_session ()
            Ok()
        | Some lease ->
            match RawInputNative.release_mouse_registration lease with
            | Ok RawInputNative.OwnRegistrationRemovedButPreviousRegistrationLost ->
                registration_release_error <-
                    Some
                        "RhinosCanFly removed its raw-mouse registration but could not restore the previous registration. Restart Rhino before flying again."

                finish_session ()
                Ok()
            | Ok _ ->
                finish_session ()
                Ok()
            | Error error ->
                if not registration_retry_timer.Enabled then
                    registration_retry_timer.Start()

                Error error

    let request_stop () =
        stop_requested <- true
        try_release_registration () |> ignore

    let release_session () =
        if not (registration_relinquished ()) then
            invalidOp "The raw-input session cannot be released while mouse registration still belongs to it."

        let release_error = registration_release_error
        active_session <- None
        registration_lease <- None
        registration_release_error <- None
        session_finished <- None
        stop_requested <- false
        session_finished_notified <- false

        match release_error with
        | Some error -> raise (InvalidOperationException error)
        | None -> ()

    let release_resources () =
        if Option.isSome active_session || Option.isSome session_finished then
            invalidOp "The raw-input worker cannot release its window while a session is active."

        if not (registration_relinquished ()) then
            invalidOp "The raw-input worker cannot release its window while mouse registration still belongs to it."

        let errors = ResizeArray<exn>()

        try
            if handle_created then
                self.DestroyHandle()
                handle_created <- false
        with error ->
            errors.Add error

        try
            if not input_buffer_disposed then
                input_buffer.Dispose()
                input_buffer_disposed <- true
        with error ->
            errors.Add error

        try
            if not registration_retry_timer_disposed then
                registration_retry_timer.Dispose()
                registration_retry_timer_disposed <- true
        with error ->
            errors.Add error

        match errors.Count with
        | 0 -> ()
        | 1 -> raise errors[0]
        | _ -> raise (AggregateException errors)

    let grow_input_buffer (minimum_capacity: uint32) =
        let current_capacity = int64 input_buffer.Capacity

        let maximum_capacity =
            int64 Int32.MaxValue - int64 (RawInputNative.RAW_INPUT_ALIGNMENT - 1)

        let doubled_capacity = min maximum_capacity (current_capacity * 2L)
        let required_capacity = max (int64 minimum_capacity) doubled_capacity

        if required_capacity <= current_capacity || required_capacity > maximum_capacity then
            invalidOp $"The raw-input buffer cannot grow to {minimum_capacity} bytes."

        input_buffer.EnsureCapacity(int required_capacity)

    let process_current_input (session: RawInputSession) (raw_input: nativeint) =
        let mutable reading = true
        let mutable work_added = false

        while reading do
            let mutable required_bytes = 0u
            let mutable error_code = 0
            let mutable mouse = Unchecked.defaultof<RawInputNative.Mouse>

            let result =
                RawInputNative.read_current_mouse raw_input input_buffer &required_bytes &error_code &mouse

            match result with
            | RawInputNative.MouseReadResult.Mouse ->
                work_added <- session.ProcessMouse mouse
                reading <- false
            | RawInputNative.MouseReadResult.Ignored -> reading <- false
            | RawInputNative.MouseReadResult.BufferTooSmall -> grow_input_buffer required_bytes
            | RawInputNative.MouseReadResult.Failed -> Win32.win32_error "GetRawInputData" error_code |> invalidOp
            | RawInputNative.MouseReadResult.Malformed -> invalidOp "GetRawInputData returned malformed mouse input."
            | _ -> invalidOp "GetRawInputData returned an unknown result."

        work_added

    let process_buffered_input (session: RawInputSession) =
        let mutable draining = true
        let mutable work_added = false

        while draining do
            let mutable buffer_bytes = 0u
            let mutable error_code = 0
            let count = RawInputNative.read_buffered input_buffer &buffer_bytes &error_code

            if count = 0u then
                draining <- false
            elif count = UInt32.MaxValue then
                if error_code = RawInputNative.ERROR_INSUFFICIENT_BUFFER then
                    grow_input_buffer buffer_bytes
                else
                    Win32.win32_error "GetRawInputBuffer" error_code |> invalidOp
            else
                let mutable index = 0u
                let mutable offset = 0

                // Keep this path on structs, ints and the reused buffer.
                while index < count do
                    if offset < 0 || offset > input_buffer.Capacity - int RawInputNative.header_size then
                        invalidOp "GetRawInputBuffer returned a record outside its buffer."

                    let record = IntPtr.Add(input_buffer.Pointer, offset)
                    let available_bytes = input_buffer.Capacity - offset
                    let mutable record_size = 0u
                    let mutable mouse = Unchecked.defaultof<RawInputNative.Mouse>

                    let result = RawInputNative.decode_mouse record available_bytes &record_size &mouse

                    match result with
                    | RawInputNative.MouseReadResult.Mouse ->
                        if session.ProcessMouse mouse then
                            work_added <- true
                    | RawInputNative.MouseReadResult.Ignored -> ()
                    | RawInputNative.MouseReadResult.Malformed ->
                        invalidOp "GetRawInputBuffer returned malformed input."
                    | RawInputNative.MouseReadResult.Failed
                    | RawInputNative.MouseReadResult.BufferTooSmall
                    | _ -> invalidOp "GetRawInputBuffer returned an unknown decode result."

                    let step = RawInputNative.aligned_record_size record_size

                    if step <= 0 || step > available_bytes then
                        invalidOp "GetRawInputBuffer returned an invalid record size."

                    offset <- offset + step
                    index <- index + 1u

        work_added

    let discard_buffered_input () =
        let mutable draining = true

        while draining do
            let mutable buffer_bytes = 0u
            let mutable error_code = 0
            let count = RawInputNative.read_buffered input_buffer &buffer_bytes &error_code

            if count = 0u then
                draining <- false
            elif count = UInt32.MaxValue then
                if error_code = RawInputNative.ERROR_INSUFFICIENT_BUFFER then
                    grow_input_buffer buffer_bytes
                else
                    Win32.win32_error "GetRawInputBuffer(discard)" error_code |> invalidOp

    let process_input_message (raw_input: nativeint) =
        match active_session with
        | Some session ->
            let current_added = process_current_input session raw_input
            let buffered_added = process_buffered_input session

            if current_added || buffered_added then
                session.SignalInputAvailable()
        | None -> ()

    let fail_runtime (error: exn) =
        match active_session with
        | Some session -> session.FailRuntime error
        | None -> Debug.WriteLine $"RhinosCanFly idle raw-input receiver failed: {error.Message}"

        request_stop ()

    do
        registration_retry_timer.Tick.Add(fun (_event: EventArgs) ->
            if stop_requested then
                try_release_registration () |> ignore)

        let parameters = CreateParams()
        parameters.Caption <- "RhinosCanFly raw input"
        parameters.Parent <- nativeint RawInputNative.MESSAGE_ONLY_WINDOW
        self.CreateHandle parameters
        handle_created <- true

    member _.WindowHandle = self.Handle

    member _.RegistrationRelinquished = registration_relinquished ()

    member _.StartSession
        (
            input: InputAccumulator.State,
            input_available: Action,
            registration_ready: Action<RawInputNative.MouseRegistrationLease>,
            runtime_failed: Action<exn>,
            finished: Action
        ) : exn option =
        if Option.isSome active_session || Option.isSome session_finished then
            invalidOp "Another raw-input session is already active."

        stop_requested <- false
        session_finished_notified <- false
        registration_release_error <- None
        session_finished <- Some finished

        try
            discard_buffered_input ()

            let session = RawInputSession(input, input_available, runtime_failed)

            active_session <- Some session

            match RawInputNative.acquire_mouse_registration self.Handle with
            | RawInputNative.Acquired lease ->
                registration_lease <- Some lease
                registration_ready.Invoke lease
                None
            | RawInputNative.Failed error -> Some(InvalidOperationException error)
            | RawInputNative.CleanupPending(error, lease) ->
                registration_lease <- Some lease
                registration_ready.Invoke lease
                Some(InvalidOperationException error)
        with error ->
            Some error

    member _.RequestStop() = request_stop ()

    member _.ReleaseSession() = release_session ()

    member _.ReleaseResources() = release_resources ()

    override _.WndProc(message: byref<Message>) =
        let mutable base_attempted = false

        try
            try
                if message.Msg = RawInputNative.CONTROL_MESSAGE then
                    message.Result <- nativeint 0
                    process_control.Invoke()
                elif message.Msg = RawInputNative.MESSAGE then
                    if not stop_requested then
                        process_input_message message.LParam

                    base_attempted <- true
                    base.WndProc(&message)
                else
                    base_attempted <- true
                    base.WndProc(&message)
            with error ->
                fail_runtime error
                message.Result <- nativeint 0
        finally
            if message.Msg = RawInputNative.MESSAGE && not base_attempted then
                try
                    base_attempted <- true
                    base.WndProc(&message)
                with cleanup_error ->
                    Debug.WriteLine $"RhinosCanFly raw-input message cleanup failed: {cleanup_error}"
