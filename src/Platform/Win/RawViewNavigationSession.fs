module RhinosCanFly.Platform.Win.RawViewNavigationSession

open System
open System.Diagnostics
open Rhino
open Rhino.Display
open Rhino.Geometry
open RhinosCanFly

let view_matches_host (host: ViewportHostIdentity) (view: RhinoView) =
    if isNull view || isNull view.Document then
        false
    else
        let (ViewWindowHandle expectedWindow) = host.view_window
        let root = Win32Native.GetAncestor(view.Handle, Win32Native.GA_ROOT)
        let actualRoot = if root = nativeint 0 then view.Handle else root
        let (RootWindow expectedRoot) = host.root_window

        view.RuntimeSerialNumber = host.view_serial_number
        && view.Document.RuntimeSerialNumber = host.document_serial_number
        && view.ActiveViewportID = host.viewport_id
        && view.Handle = expectedWindow
        && actualRoot = expectedRoot

let hostValidationIntervalTicks = max 1L (Stopwatch.Frequency / 10L)

type Session
    internal
    (
        host: ViewportHostIdentity,
        mode: ViewportNavigation.Operation,
        input: InputAccumulator.State,
        wake: RawInputWake.State,
        raw: RawInputThread.Session,
        view: RhinoView,
        viewport: RhinoViewport,
        mouseConfig: ViewportNavigation.MouseConfig,
        initialPivotCenter: Point3d,
        originalCursor: System.Drawing.Point,
        cursorClip: CursorClipLease,
        buttonEvents: Func<RawMouseButtonEvent, System.Drawing.Point, bool>,
        failed: Action
    ) as self =

    let mutable active = true
    let mutable draining = false
    let mutable subscribed = false
    let mutable cursorHidden = true
    let mutable failureNotified = false
    let mutable rawInputClean = false
    let mutable clipReleased = false
    let mutable cursorRestored = false
    let mutable wakeDisposed = false
    let mutable wheelRemainder = 0L
    let mutable parallelZoomExponentRemainder = 0.
    let mutable pivotCenter = initialPivotCenter
    let timeline = InputAccumulator.timeline_buffer ()

    let parallelZoomMode =
        match mode with
        | ViewportNavigation.Operation.ParallelZoom -> true
        | ViewportNavigation.Operation.Pivot
        | ViewportNavigation.Operation.Pan
        | ViewportNavigation.Operation.ParallelPan -> false

    let mutable nextHostValidationAt =
        Stopwatch.GetTimestamp() + hostValidationIntervalTicks

    let mainLoopHandler = EventHandler(fun (_: obj) (_: EventArgs) -> self.Drain())

    let apply_movement (dx: int64) (dy: int64) (wheelDelta: int64) =
        let wheel = wheelRemainder + wheelDelta
        let wheelSteps = wheel / int64 Win32Native.WHEEL_DELTA
        wheelRemainder <- wheel - wheelSteps * int64 Win32Native.WHEEL_DELTA

        let parallelZoomPending = parallelZoomMode && parallelZoomExponentRemainder <> 0.

        if dx <> 0L || dy <> 0L || wheelSteps <> 0L || parallelZoomPending then
            let now = Stopwatch.GetTimestamp()
            let hostValidationDue = now >= nextHostValidationAt

            if hostValidationDue then
                nextHostValidationAt <- now + hostValidationIntervalTicks

            if hostValidationDue && not (view_matches_host host view) then
                self.NotifyFailure()
            else
                let movementChanged =
                    if dx = 0L && dy = 0L && not parallelZoomPending then
                        false
                    else
                        match mode with
                        | ViewportNavigation.Operation.Pivot ->
                            ViewportNavigation.apply_pivot viewport mouseConfig pivotCenter dx dy
                        | ViewportNavigation.Operation.Pan
                        | ViewportNavigation.Operation.ParallelPan ->
                            ViewportNavigation.apply_pan viewport mouseConfig dx dy
                        | ViewportNavigation.Operation.ParallelZoom ->
                            let requestedExponent =
                                parallelZoomExponentRemainder + ViewportNavigation.parallel_zoom_exponent dy

                            if requestedExponent = 0. then
                                false
                            else
                                let appliedExponent = max -0.25 (min 0.25 requestedExponent)

                                if viewport.Magnify(Math.Exp appliedExponent, true) then
                                    let remaining = requestedExponent - appliedExponent

                                    parallelZoomExponentRemainder <-
                                        if abs remaining < 0.000000000001 then 0. else remaining

                                    true
                                else
                                    parallelZoomExponentRemainder <- 0.
                                    Debug.WriteLine "RhinosCanFly parallel zoom was rejected by Rhino."
                                    self.NotifyFailure()
                                    false

                let wheelChanged =
                    if wheelSteps = 0L then
                        false
                    else
                        let magnification = ViewportNavigation.wheel_magnification wheelSteps

                        magnification <> 1.
                        && viewport.Magnify(magnification, viewport.IsParallelProjection)

                if movementChanged || wheelChanged then
                    view.Redraw()

    member _.NotifyFailure() =
        if active && not failureNotified then
            failureNotified <- true
            failed.Invoke()

    member this.Drain() =
        if active && not draining then
            draining <- true
            let observedRevision = InputAccumulator.work_revision input

            try
                try
                    if RawInputThread.runtime_failed raw then
                        this.NotifyFailure()
                    else
                        let struct (timelineCount, timelineOverflowed) =
                            InputAccumulator.drain_timeline timeline input

                        if timelineOverflowed then
                            Debug.WriteLine "RhinosCanFly raw view navigation timeline overflowed."
                            this.NotifyFailure()
                        else
                            let mutable acceptMovement = true
                            let mutable timelineIndex = 0

                            while active && not failureNotified && timelineIndex < timelineCount do
                                let event = timeline[timelineIndex]

                                match event.kind with
                                | InputAccumulator.TimelineEventKind.Movement when acceptMovement ->
                                    apply_movement event.dx event.dy event.wheel
                                | InputAccumulator.TimelineEventKind.RawMouseButton ->
                                    acceptMovement <- buttonEvents.Invoke(event.button.event, originalCursor)
                                | InputAccumulator.TimelineEventKind.Movement
                                | InputAccumulator.TimelineEventKind.KeyboardActions -> ()
                                | _ -> invalidOp "The raw view navigation timeline contains an unknown event."

                                timelineIndex <- timelineIndex + 1

                            if acceptMovement && parallelZoomMode && parallelZoomExponentRemainder <> 0. then
                                apply_movement 0L 0L 0L
                with error ->
                    Debug.WriteLine $"RhinosCanFly raw view navigation failed: {error}"
                    this.NotifyFailure()
            finally
                RawInputWake.acknowledge wake

                if
                    active
                    && not failureNotified
                    && (parallelZoomExponentRemainder <> 0.
                        || InputAccumulator.work_pending_since observedRevision input)
                then
                    RawInputWake.signal wake

                draining <- false

    member internal _.Attach() =
        if active && not subscribed then
            RhinoApp.MainLoop.AddHandler mainLoopHandler
            subscribed <- true

    member _.Host = host
    member _.Mode = mode
    member _.IsActive = active

    member _.CleanupComplete =
        not active
        && not subscribed
        && rawInputClean
        && clipReleased
        && cursorRestored
        && not cursorHidden
        && wakeDisposed

    member _.RawInputRegistrationIsCurrent() =
        RawInputThread.registration_is_current raw

    member _.UpdatePivotCenter(updated: Point3d voption) =
        match updated with
        | ValueSome center when center.IsValid -> pivotCenter <- center
        | ValueSome _
        | ValueNone -> ()

    member _.Matches(expectedHost: ViewportHostIdentity, expectedMode: ViewportNavigation.Operation) =
        active && host = expectedHost && mode = expectedMode

    member _.Stop() =
        if active then
            active <- false

        let errors = ResizeArray<string>()

        if subscribed then
            try
                RhinoApp.MainLoop.RemoveHandler mainLoopHandler
                subscribed <- false
            with error ->
                errors.Add $"main-loop handler: {error.Message}"

        if not rawInputClean then
            match RawInputThread.request_stop raw with
            | Ok() -> ()
            | Error error -> errors.Add $"raw-input stop request: {error}"

        if not clipReleased then
            match Win32.release_cursor_clip cursorClip with
            | Ok() -> clipReleased <- true
            | Error error -> errors.Add $"cursor clip: {error}"

        if not rawInputClean then
            let outcome = RawInputThread.stop raw

            rawInputClean <-
                outcome.terminated
                && outcome.registration_relinquished
                && not outcome.previous_registration_lost

            if not rawInputClean then
                errors.Add "raw input did not shut down cleanly"

            for error in outcome.errors do
                errors.Add $"raw input: {error}"

        if not cursorRestored then
            let (RootWindow rootWindow) = host.root_window

            if
                rootWindow <> nativeint 0
                && Win32Native.IsWindow rootWindow
                && Win32Native.GetForegroundWindow() = rootWindow
            then
                match Win32.set_cursor_position originalCursor with
                | Ok() -> cursorRestored <- true
                | Error error -> errors.Add $"cursor position: {error}"
            else
                cursorRestored <- true

        if cursorHidden then
            Win32Native.ShowCursor true |> ignore
            cursorHidden <- false

        if not wakeDisposed then
            RawInputWake.dispose wake
            wakeDisposed <- true

        if errors.Count = 0 then
            Ok()
        else
            Error(String.Join("; ", errors))

let start
    (host: ViewportHostIdentity)
    (mode: ViewportNavigation.Operation)
    (requestedPivotCenter: Point3d voption)
    (mouseConfig: ViewNavigationMouseConfig)
    (buttonEvents: Func<RawMouseButtonEvent, System.Drawing.Point, bool>)
    (failed: Action)
    =
    let view = RhinoView.FromRuntimeSerialNumber host.view_serial_number

    if not (view_matches_host host view) then
        Error "The navigation viewport is unavailable."
    else
        let viewport = view.ActiveViewport

        let requiresParallelProjection =
            mode = ViewportNavigation.Operation.ParallelPan
            || mode = ViewportNavigation.Operation.ParallelZoom

        if requiresParallelProjection && not viewport.IsParallelProjection then
            Error "The viewport is no longer using parallel projection."
        else
            let activeMouseConfig =
                ViewportNavigation.mouse_config mouseConfig viewport.IsParallelProjection

            let pivotCenter =
                match requestedPivotCenter with
                | ValueSome center when center.IsValid -> center
                | ValueSome _
                | ValueNone -> viewport.CameraTarget

            match Win32.get_cursor_position () with
            | Error error -> Error error
            | Ok originalCursor ->
                let input = InputAccumulator.create ()
                let wake = RawInputWake.create host.root_window
                let inputAvailable = Action(fun () -> RawInputWake.signal wake)

                let mutable raw: RawInputThread.Session option = None
                let mutable cursorClip: CursorClipLease option = None
                let mutable cursorHidden = false

                try
                    let createdRaw = RawInputThread.start input inputAvailable
                    raw <- Some createdRaw

                    match Win32.acquire_cursor_clip view.ScreenRectangle with
                    | Error error -> failwith error
                    | Ok lease -> cursorClip <- Some lease

                    Win32Native.ShowCursor false |> ignore
                    cursorHidden <- true

                    match cursorClip with
                    | Some lease ->
                        let session =
                            Session(
                                host,
                                mode,
                                input,
                                wake,
                                createdRaw,
                                view,
                                viewport,
                                activeMouseConfig,
                                pivotCenter,
                                originalCursor,
                                lease,
                                buttonEvents,
                                failed
                            )

                        session.Attach()

                        // Ignore startup movement but keep button releases from the same gesture.
                        let startupRevision = InputAccumulator.work_revision input
                        InputAccumulator.discard_movement input
                        RawInputWake.acknowledge wake

                        if InputAccumulator.work_pending_since startupRevision input then
                            RawInputWake.signal wake

                        Ok session
                    | None -> failwith "The navigation cursor clip was not acquired."
                with error ->
                    match cursorClip with
                    | Some lease -> Win32.release_cursor_clip lease |> ignore
                    | None -> ()

                    match raw with
                    | Some created -> RawInputThread.stop created |> ignore
                    | None -> ()

                    if cursorHidden then
                        Win32Native.ShowCursor true |> ignore

                    RawInputWake.dispose wake
                    Error $"Could not start raw view navigation: {error.Message}"
