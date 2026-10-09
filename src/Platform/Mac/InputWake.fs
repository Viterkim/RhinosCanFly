module RhinosCanFly.MacInputWake

open System
open System.Runtime.InteropServices
open System.Threading

type State(signal: unit -> unit, run: unit -> unit) =
    let gate = obj ()
    let mutable demand = false
    let mutable queued = false
    let mutable processing = false
    let mutable yielding = false
    let mutable awaiting_key = false
    let mutable stopped = false

    member _.Request(resume: bool) =
        Monitor.Enter gate

        try
            if not stopped then
                demand <- true

                if resume then
                    awaiting_key <- false

                if not (queued || processing || yielding || awaiting_key) then
                    queued <- true

                    try
                        signal ()
                    with _ ->
                        queued <- false
                        reraise ()
        finally
            Monitor.Exit gate

    member _.Dispatch() =
        Monitor.Enter gate

        let deliver =
            try
                queued <- false

                if demand && not (stopped || awaiting_key) then
                    yielding <- true

                // Delivery can be stale if a host-led pulse already consumed its work.
                demand && not (stopped || awaiting_key)
            finally
                Monitor.Exit gate

        if deliver then
            run ()

    member _.TryBegin() =
        // Host-led pulses may run while yielding or awaiting a key. Only source signalling is deferred.
        Monitor.Enter gate

        try
            if stopped || processing then
                false
            else
                processing <- true
                yielding <- true
                demand <- false
                true
        finally
            Monitor.Exit gate

    member _.Complete(continue_work: bool, wait_for_key: bool) =
        Monitor.Enter gate

        try
            processing <- false

            if not stopped then
                demand <- demand || continue_work
                awaiting_key <- wait_for_key
                yielding <- true
        finally
            Monitor.Exit gate

    member _.Boundary() =
        Monitor.Enter gate

        try
            if not (stopped || processing) then
                yielding <- false

                if demand && not (queued || awaiting_key) then
                    queued <- true

                    try
                        signal ()
                    with _ ->
                        queued <- false
                        reraise ()
        finally
            Monitor.Exit gate

    member _.Stop() =
        Monitor.Enter gate

        try
            stopped <- true
            demand <- false
        finally
            Monitor.Exit gate

module Native =
    [<Literal>]
    let CORE_FOUNDATION =
        "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation"

    [<UnmanagedFunctionPointer(CallingConvention.Cdecl)>]
    type Perform = delegate of nativeint -> unit

    [<UnmanagedFunctionPointer(CallingConvention.Cdecl)>]
    type Observe = delegate of nativeint * unativeint * nativeint -> unit

    [<Struct; StructLayout(LayoutKind.Sequential)>]
    type SourceContext =
        val version: nativeint
        val info: nativeint
        val retain: nativeint
        val release: nativeint
        val copy_description: nativeint
        val equal: nativeint
        val hash: nativeint
        val schedule: nativeint
        val cancel: nativeint
        val perform: nativeint

        new(perform: nativeint) =
            { version = 0n
              info = 0n
              retain = 0n
              release = 0n
              copy_description = 0n
              equal = 0n
              hash = 0n
              schedule = 0n
              cancel = 0n
              perform = perform }

    [<Struct; StructLayout(LayoutKind.Sequential)>]
    type ObserverContext =
        val version: nativeint
        val info: nativeint
        val retain: nativeint
        val release: nativeint
        val copy_description: nativeint

    [<DllImport(CORE_FOUNDATION)>]
    extern nativeint CFRunLoopGetMain()

    [<DllImport(CORE_FOUNDATION)>]
    extern nativeint CFRunLoopGetCurrent()

    [<DllImport(CORE_FOUNDATION)>]
    extern nativeint CFRunLoopSourceCreate(nativeint allocator, nativeint order, SourceContext& context)

    [<DllImport(CORE_FOUNDATION)>]
    extern void CFRunLoopAddSource(nativeint loop, nativeint source, nativeint mode)

    [<DllImport(CORE_FOUNDATION)>]
    extern void CFRunLoopSourceSignal(nativeint source)

    [<DllImport(CORE_FOUNDATION)>]
    extern void CFRunLoopWakeUp(nativeint loop)

    [<DllImport(CORE_FOUNDATION)>]
    extern void CFRunLoopSourceInvalidate(nativeint source)

    [<DllImport(CORE_FOUNDATION)>]
    extern nativeint CFRunLoopObserverCreate(
        nativeint allocator,
        unativeint activities,
        [<MarshalAs(UnmanagedType.I1)>] bool repeats,
        nativeint order,
        Observe callback,
        ObserverContext& context
    )

    [<DllImport(CORE_FOUNDATION)>]
    extern void CFRunLoopAddObserver(nativeint loop, nativeint observer, nativeint mode)

    [<DllImport(CORE_FOUNDATION)>]
    extern void CFRunLoopObserverInvalidate(nativeint observer)

    [<DllImport(CORE_FOUNDATION)>]
    extern void CFRelease(nativeint value)

type Connection internal (state: State, close: unit -> unit, callback: Delegate, observer_callback: Delegate) =
    member _.Request() = state.Request true
    member _.RequestMotion() = state.Request false
    member _.TryBegin() = state.TryBegin()

    member _.Complete(continue_work: bool, wait_for_key: bool) =
        state.Complete(continue_work, wait_for_key)

    interface IDisposable with
        member _.Dispose() =
            state.Stop()
            close ()
            GC.KeepAlive callback
            GC.KeepAlive observer_callback

let create (run: unit -> unit) (on_error: exn -> unit) =
    let gate = obj ()
    let loop = Native.CFRunLoopGetMain()
    let mutable source = 0n
    let mutable observer = 0n

    if Native.CFRunLoopGetCurrent() <> loop then
        invalidOp "Mac navigation scheduling must start on the OS main thread."

    let signal () =
        Monitor.Enter gate

        try
            if source <> 0n then
                Native.CFRunLoopSourceSignal source
                Native.CFRunLoopWakeUp loop
        finally
            Monitor.Exit gate

    let state = State(signal, run)
    let mutable callback: Native.Perform = null

    callback <-
        Native.Perform(fun (_info: nativeint) ->
            let executing = callback

            try
                try
                    state.Dispatch()
                with error ->
                    // Managed exceptions must stay inside the native callback.
                    try
                        on_error error
                    with _ ->
                        ()
            finally
                // Flight exit can dispose the source from inside this callback.
                GC.KeepAlive executing)

    let mutable observer_callback: Native.Observe = null

    observer_callback <-
        Native.Observe(fun (_observer: nativeint) (_activity: unativeint) (_info: nativeint) ->
            let executing = observer_callback

            try
                try
                    state.Boundary()
                with error ->
                    try
                        on_error error
                    with _ ->
                        ()
            finally
                GC.KeepAlive executing)

    let close () =
        Monitor.Enter gate

        try
            if observer <> 0n then
                Native.CFRunLoopObserverInvalidate observer
                Native.CFRelease observer
                observer <- 0n

            if source <> 0n then
                Native.CFRunLoopSourceInvalidate source
                Native.CFRelease source
                source <- 0n
        finally
            Monitor.Exit gate

    try
        let library = NativeLibrary.Load Native.CORE_FOUNDATION

        let mode =
            try
                Marshal.ReadIntPtr(NativeLibrary.GetExport(library, "kCFRunLoopDefaultMode"))
            finally
                NativeLibrary.Free library

        let mutable context =
            Native.SourceContext(Marshal.GetFunctionPointerForDelegate callback)

        source <- Native.CFRunLoopSourceCreate(0n, 0n, &context)

        if source = 0n then
            invalidOp "CoreFoundation could not create the navigation wake source."

        Native.CFRunLoopAddSource(loop, source, mode)
        let mutable observer_context = Unchecked.defaultof<Native.ObserverContext>

        // Rearm late in beforeWaiting; observer order does not establish frame completion.
        observer <-
            Native.CFRunLoopObserverCreate(
                0n,
                1un <<< 5,
                true,
                nativeint Int64.MaxValue,
                observer_callback,
                &observer_context
            )

        if observer = 0n then
            invalidOp "CoreFoundation could not create the navigation continuation observer."

        Native.CFRunLoopAddObserver(loop, observer, mode)
        new Connection(state, close, callback, observer_callback)
    with _ ->
        state.Stop()
        close ()
        GC.KeepAlive callback
        GC.KeepAlive observer_callback
        reraise ()
