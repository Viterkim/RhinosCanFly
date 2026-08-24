module RhinosCanFly.Platform.Win.RawViewNavigation

open System
open System.Diagnostics
open Rhino
open Rhino.ApplicationSettings
open Rhino.Display
open Rhino.Geometry
open RhinosCanFly

[<Struct>]
type Mode =
    | Pivot
    | Pan
    | ParallelPan
    | ParallelZoom

[<Struct>]
type ActiveMouseConfig =
    { x_mode: MouseAxisMode
      y_mode: MouseAxisMode
      sensitivity: RuntimeMouseSensitivity
      pivot_multiplier: MousePivotMultiplier
      pan_multiplier: MousePanMultiplier }

let active_mouse_config (config: ViewNavigationMouseConfig) (isParallel: bool) =
    if isParallel then
        { x_mode = config.x_mode
          y_mode = config.y_mode
          sensitivity = config.parallel_sensitivity
          pivot_multiplier = config.parallel_pivot_multiplier
          pan_multiplier = config.parallel_pan_multiplier }
    else
        { x_mode = config.x_mode
          y_mode = config.y_mode
          sensitivity = config.perspective_sensitivity
          pivot_multiplier = config.perspective_pivot_multiplier
          pan_multiplier = config.perspective_pan_multiplier }

let pivot (viewport: RhinoViewport) (config: ActiveMouseConfig) (center: Point3d) (dx: int64) (dy: int64) =
    let (MousePivotMultiplier multiplier) = config.pivot_multiplier

    let deltas =
        Movement.mouse_angle_deltas config.x_mode config.y_mode config.sensitivity multiplier dx dy

    let mutable changed = false

    if
        deltas.yaw_delta <> 0.
        && viewport.Rotate(deltas.yaw_delta, Vector3d.ZAxis, center)
    then
        changed <- true

    if deltas.pitch_delta <> 0. then
        let mutable right = viewport.CameraX

        if right.Unitize() && viewport.Rotate(deltas.pitch_delta, right, center) then
            changed <- true

    changed

let pan (viewport: RhinoViewport) (config: ActiveMouseConfig) (dx: int64) (dy: int64) =
    let (MousePanMultiplier multiplier) = config.pan_multiplier

    let deltas =
        Movement.mouse_angle_deltas config.x_mode config.y_mode config.sensitivity multiplier dx dy

    let location = viewport.CameraLocation
    let target = viewport.CameraTarget
    let mutable direction = viewport.CameraDirection
    let mutable right = viewport.CameraX
    let mutable up = viewport.CameraY

    if direction.Unitize() && right.Unitize() && up.Unitize() then
        let requestedDepth = Vector3d.Multiply(target - location, direction)

        let depth =
            if
                RhinoMath.IsValidDouble requestedDepth
                && requestedDepth > RhinoMath.ZeroTolerance
            then
                requestedDepth
            else
                1.

        let translation = right * deltas.yaw_delta * depth - up * deltas.pitch_delta * depth

        if translation.IsZero then
            false
        else
            viewport.SetCameraLocation(location + translation, false)
            viewport.SetCameraTarget(target + translation, false)
            true
    else
        false

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

let parallel_zoom_exponent (dy: int64) =
    let zoomScale = ViewSettings.ZoomScale

    if
        dy <> 0L
        && not (Double.IsNaN zoomScale)
        && not (Double.IsInfinity zoomScale)
        && zoomScale > 0.
        && zoomScale <> 1.
    then
        let steps = float -dy / 12.
        steps * Math.Log(1. / zoomScale)
    else
        0.

let wheel_magnification (steps: int64) =
    let zoomScale = ViewSettings.ZoomScale

    if
        steps <> 0L
        && not (Double.IsNaN zoomScale)
        && not (Double.IsInfinity zoomScale)
        && zoomScale > 0.
    then
        let magnification = Math.Pow(1. / zoomScale, float steps)

        if
            Double.IsNaN magnification
            || Double.IsInfinity magnification
            || magnification <= 0.
        then
            1.
        else
            magnification
    else
        1.

type Session
    internal
    (
        host: ViewportHostIdentity,
        mode: Mode,
        input: InputAccumulator.State,
        wake: RawInputWake.State,
        raw: RawInputThread.Session,
        view: RhinoView,
        viewport: RhinoViewport,
        mouseConfig: ActiveMouseConfig,
        initialPivotCenter: Point3d,
        originalCursor: System.Drawing.Point,
        cursorClip: CursorClipLease,
        buttonEvents: Action<RawMouseButtonEvent, System.Drawing.Point>,
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

    let parallelZoomMode =
        match mode with
        | Mode.ParallelZoom -> true
        | Mode.Pivot
        | Mode.Pan
        | Mode.ParallelPan -> false

    let mutable nextHostValidationAt =
        Stopwatch.GetTimestamp() + hostValidationIntervalTicks

    let mainLoopHandler = EventHandler(fun (_: obj) (_: EventArgs) -> self.Drain())

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
                    let struct (dx, dy) = InputAccumulator.drain_mouse input
                    let wheel = wheelRemainder + InputAccumulator.drain_wheel input
                    let wheelSteps = wheel / int64 Win32Native.WHEEL_DELTA
                    wheelRemainder <- wheel - wheelSteps * int64 Win32Native.WHEEL_DELTA

                    let parallelZoomPending = parallelZoomMode && parallelZoomExponentRemainder <> 0.

                    if RawInputThread.runtime_failed raw then
                        this.NotifyFailure()
                    elif dx <> 0L || dy <> 0L || wheelSteps <> 0L || parallelZoomPending then
                        let now = Stopwatch.GetTimestamp()
                        let hostValidationDue = now >= nextHostValidationAt

                        if hostValidationDue then
                            nextHostValidationAt <- now + hostValidationIntervalTicks

                        if hostValidationDue && not (view_matches_host host view) then
                            this.NotifyFailure()
                        else
                            let movementChanged =
                                if dx = 0L && dy = 0L && not parallelZoomPending then
                                    false
                                else
                                    match mode with
                                    | Mode.Pivot -> pivot viewport mouseConfig pivotCenter dx dy
                                    | Mode.Pan
                                    | Mode.ParallelPan -> pan viewport mouseConfig dx dy
                                    | Mode.ParallelZoom ->
                                        let requestedExponent =
                                            parallelZoomExponentRemainder + parallel_zoom_exponent dy

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
                                                this.NotifyFailure()
                                                false

                            let wheelChanged =
                                if wheelSteps = 0L then
                                    false
                                else
                                    let magnification = wheel_magnification wheelSteps

                                    magnification <> 1.
                                    && viewport.Magnify(magnification, viewport.IsParallelProjection)

                            if movementChanged || wheelChanged then
                                view.Redraw()

                    let mutable drainingButtons = true

                    while drainingButtons do
                        match InputAccumulator.try_drain_raw_mouse_button_event input with
                        | ValueSome transition -> buttonEvents.Invoke(transition.event, originalCursor)
                        | ValueNone -> drainingButtons <- false

                    if InputAccumulator.drain_raw_mouse_button_event_overflow input then
                        Debug.WriteLine "RhinosCanFly raw view navigation button queue overflowed."
                        this.NotifyFailure()
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

    member _.Matches(expectedHost: ViewportHostIdentity, expectedMode: Mode) =
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
    (mode: Mode)
    (requestedPivotCenter: Point3d voption)
    (mouseConfig: ViewNavigationMouseConfig)
    (buttonEvents: Action<RawMouseButtonEvent, System.Drawing.Point>)
    (failed: Action)
    =
    let view = RhinoView.FromRuntimeSerialNumber host.view_serial_number

    if not (view_matches_host host view) then
        Error "The navigation viewport is unavailable."
    else
        let viewport = view.ActiveViewport

        let requiresParallelProjection = mode = Mode.ParallelPan || mode = Mode.ParallelZoom

        if requiresParallelProjection && not viewport.IsParallelProjection then
            Error "The viewport is no longer using parallel projection."
        else
            let activeMouseConfig =
                active_mouse_config mouseConfig viewport.IsParallelProjection

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

                let rawConfig: RawInputConfig =
                    { exit_on_mouse_left = false
                      exit_on_mouse_right = false
                      middle_mouse_action = RoutedMouseAction.Off
                      mouse4_action = RoutedMouseAction.Off
                      mouse5_action = RoutedMouseAction.Off }

                let sessionMode = FlightSessionMode.until_exit FlightMode.Normal
                let mutable raw: RawInputThread.Session option = None
                let mutable cursorClip: CursorClipLease option = None
                let mutable cursorHidden = false

                try
                    let createdRaw = RawInputThread.start rawConfig sessionMode input inputAvailable
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
                        InputAccumulator.drain_mouse input |> ignore
                        InputAccumulator.drain_wheel input |> ignore
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
