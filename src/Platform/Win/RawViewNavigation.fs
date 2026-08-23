module RhinosCanFly.Platform.Win.RawViewNavigation

open System
open System.Collections.Generic
open System.Diagnostics
open System.Drawing
open Rhino
open Rhino.ApplicationSettings
open Rhino.Display
open RhinosCanFly

[<Struct>]
type Mode =
    | Pivot
    | Pan
    | ParallelZoom

let coordinate (origin: int) (delta: int64) =
    let value = int64 origin + delta

    if value > int64 Int32.MaxValue then Int32.MaxValue
    elif value < int64 Int32.MinValue then Int32.MinValue
    else int value

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

let zoom_parallel (viewport: RhinoViewport) (dy: int64) =
    let zoomScale = ViewSettings.ZoomScale

    if
        dy <> 0L
        && not (Double.IsNaN zoomScale)
        && not (Double.IsInfinity zoomScale)
        && zoomScale > 0.
        && zoomScale <> 1.
    then
        let steps = float -dy / 12.
        let rawExponent = steps * Math.Log(1. / zoomScale)
        let exponent = max -0.25 (min 0.25 rawExponent)
        viewport.Magnify(Math.Exp exponent, true)
    else
        false

type Session
    internal
    (
        host: ViewportHostIdentity,
        mode: Mode,
        input: InputAccumulator.State,
        wake: RawInputWake.State,
        raw: RawInputThread.Session,
        originalCursor: Point,
        cursorClip: CursorClipLease,
        buttonEvents: Action<RawMouseButtonEvents, Point>,
        failed: Action
    ) as self =

    let mutable active = true
    let mutable subscribed = false
    let mutable cursorHidden = true
    let mutable failureNotified = false
    let mutable rawStopped = false
    let mutable clipReleased = false
    let mutable cursorRestored = false
    let mutable wakeDisposed = false

    let mainLoopHandler = EventHandler(fun (_: obj) (_: EventArgs) -> self.Drain())

    member _.NotifyFailure() =
        if active && not failureNotified then
            failureNotified <- true
            failed.Invoke()

    member this.Drain() =
        if active then
            try
                let observedRevision = InputAccumulator.work_revision input
                let struct (dx, dy) = InputAccumulator.drain_mouse input
                let buttons = InputAccumulator.drain_raw_mouse_button_events input
                InputAccumulator.drain_wheel input |> ignore

                if RawInputThread.runtime_failed raw then
                    this.NotifyFailure()
                elif dx <> 0L || dy <> 0L then
                    let view = RhinoView.FromRuntimeSerialNumber host.view_serial_number

                    if not (view_matches_host host view) then
                        this.NotifyFailure()
                    else
                        let viewport = view.ActiveViewport
                        let bounds = viewport.Bounds
                        let center = Point(bounds.Width / 2, bounds.Height / 2)
                        let current = Point(coordinate center.X dx, coordinate center.Y dy)

                        let changed =
                            match mode with
                            | Mode.Pivot -> viewport.MouseRotateAroundTarget(center, current)
                            | Mode.Pan -> viewport.MouseLateralDolly(center, current)
                            | Mode.ParallelZoom -> zoom_parallel viewport dy

                        if changed then
                            view.Redraw()

                if buttons <> RawMouseButtonEvents.None then
                    buttonEvents.Invoke(buttons, originalCursor)

                RawInputWake.acknowledge wake

                if InputAccumulator.work_pending_since observedRevision input then
                    RawInputWake.signal wake
            with error ->
                Debug.WriteLine $"RhinosCanFly raw view navigation failed: {error}"
                this.NotifyFailure()

    member internal _.Attach() =
        if active && not subscribed then
            RhinoApp.MainLoop.AddHandler mainLoopHandler
            subscribed <- true

    member _.Host = host
    member _.Mode = mode
    member _.IsActive = active

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

        if not rawStopped then
            match RawInputThread.request_stop raw with
            | Ok() -> ()
            | Error error -> errors.Add $"raw-input stop request: {error}"

        if not clipReleased then
            match Win32.release_cursor_clip cursorClip with
            | Ok() -> clipReleased <- true
            | Error error -> errors.Add $"cursor clip: {error}"

        if not rawStopped then
            let outcome = RawInputThread.stop raw
            rawStopped <- true

            if
                not outcome.terminated
                || not outcome.registration_relinquished
                || outcome.previous_registration_lost
            then
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

        if rawStopped && not wakeDisposed then
            RawInputWake.dispose wake
            wakeDisposed <- true

        if errors.Count = 0 then
            Ok()
        else
            Error(String.Join("; ", errors))

let start
    (host: ViewportHostIdentity)
    (mode: Mode)
    (buttonEvents: Action<RawMouseButtonEvents, Point>)
    (failed: Action)
    =
    let view = RhinoView.FromRuntimeSerialNumber host.view_serial_number

    if not (view_matches_host host view) then
        Error "The navigation viewport is unavailable."
    else
        match Win32.get_cursor_position () with
        | Error error -> Error error
        | Ok originalCursor ->
            let input = InputAccumulator.create ()
            let wake = RawInputWake.create host.root_window
            let inputAvailable = Action(fun () -> RawInputWake.signal wake)

            let rawConfig: RawInputConfig =
                { capture_button_events = true
                  exit_on_mouse_left = false
                  exit_on_mouse_right = false
                  middle_mouse_while_flying = FlyingMiddleMouseMode.Off
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
                        Session(host, mode, input, wake, createdRaw, originalCursor, lease, buttonEvents, failed)

                    session.Attach()
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
