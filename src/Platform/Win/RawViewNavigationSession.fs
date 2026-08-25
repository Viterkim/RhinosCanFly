module RhinosCanFly.Platform.Win.RawViewNavigationSession

open System
open System.Diagnostics
open Rhino
open Rhino.Display
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

[<Struct>]
type DrainResult = { count: int; overflowed: bool }

type Session
    internal
    (
        host: ViewportHostIdentity,
        mode: ViewportNavigation.Operation,
        input: InputAccumulator.State,
        wake: PlatformInputWake.State,
        raw: PlatformRawInput.Session,
        view: RhinoView,
        viewport: RhinoViewport,
        originalCursor: System.Drawing.Point,
        cursorClip: CursorClipLease,
        failed: Action
    ) =

    let mutable active = true
    let mutable draining = false
    let mutable mainLoopHandler: EventHandler option = None
    let mutable failureNotified = false
    let mutable rawInputClean = false
    let mutable clipReleased = false
    let mutable cursorRestored = false
    let mutable cursorHidden = true
    let mutable wakeDisposed = false

    member _.NotifyFailure() =
        if active && not failureNotified then
            failureNotified <- true
            failed.Invoke()

    member _.Host = host
    member _.Mode = mode
    member _.View = view
    member _.Viewport = viewport
    member _.OriginalCursor = originalCursor
    member _.IsActive = active
    member _.FailureNotified = failureNotified

    member _.CleanupComplete =
        not active
        && Option.isNone mainLoopHandler
        && rawInputClean
        && clipReleased
        && cursorRestored
        && not cursorHidden
        && wakeDisposed

    member _.RawInputRegistrationIsCurrent() =
        PlatformRawInput.registration_is_current raw

    member _.Matches(expectedHost: ViewportHostIdentity, expectedMode: ViewportNavigation.Operation) =
        active && host = expectedHost && mode = expectedMode

    member _.DiscardPointerInput() =
        InputAccumulator.discard_pointer_input input

    member _.RequestDrain() =
        if active then
            PlatformInputWake.signal wake

    member this.Drain(destination: InputAccumulator.TimelineEvent array) =
        if not active || draining then
            ValueNone
        else
            draining <- true
            let observedRevision = InputAccumulator.work_revision input

            try
                try
                    if PlatformRawInput.runtime_failed raw then
                        this.NotifyFailure()
                        ValueNone
                    else
                        let struct (count, overflowed) = InputAccumulator.drain_timeline destination input

                        ValueSome
                            { count = count
                              overflowed = overflowed }
                with error ->
                    Debug.WriteLine $"RhinosCanFly raw view navigation transport failed: {error}"
                    this.NotifyFailure()
                    ValueNone
            finally
                PlatformInputWake.acknowledge wake

                if
                    active
                    && not failureNotified
                    && InputAccumulator.work_pending_since observedRevision input
                then
                    PlatformInputWake.signal wake

                draining <- false

    member this.Attach(workAvailable: Action) =
        if active && Option.isNone mainLoopHandler then
            let handler =
                EventHandler(fun (_: obj) (_: EventArgs) ->
                    try
                        workAvailable.Invoke()
                    with error ->
                        Debug.WriteLine $"RhinosCanFly raw view navigation UI work failed: {error}"
                        this.NotifyFailure())

            RhinoApp.MainLoop.AddHandler handler
            mainLoopHandler <- Some handler

    member _.Stop() =
        active <- false
        let errors = ResizeArray<string>()

        match mainLoopHandler with
        | Some handler ->
            try
                RhinoApp.MainLoop.RemoveHandler handler
                mainLoopHandler <- None
            with error ->
                errors.Add $"main-loop handler: {error.Message}"
        | None -> ()

        if not rawInputClean then
            match PlatformRawInput.request_stop raw with
            | Ok() -> ()
            | Error error -> errors.Add $"raw-input stop request: {error}"

        if not clipReleased then
            match PlatformCursorClip.release cursorClip with
            | Ok() -> clipReleased <- true
            | Error error -> errors.Add $"cursor clip: {error}"

        if not rawInputClean then
            let outcome = PlatformRawInput.stop raw

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
            PlatformInputWake.dispose wake
            wakeDisposed <- true

        if errors.Count = 0 then
            Ok()
        else
            Error(String.Join("; ", errors))

let start (host: ViewportHostIdentity) (mode: ViewportNavigation.Operation) (failed: Action) =
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
            match Win32.get_cursor_position () with
            | Error error -> Error error
            | Ok originalCursor ->
                let input = InputAccumulator.create ()
                let wake = PlatformInputWake.create host.root_window
                let inputAvailable = Action(fun () -> PlatformInputWake.signal wake)
                let mutable raw: PlatformRawInput.Session option = None
                let mutable cursorClip: CursorClipLease option = None
                let mutable cursorHidden = false

                try
                    let createdRaw = PlatformRawInput.start input inputAvailable
                    raw <- Some createdRaw

                    match PlatformCursorClip.acquire view with
                    | Error error -> failwith error
                    | Ok lease -> cursorClip <- Some lease

                    Win32Native.ShowCursor false |> ignore
                    cursorHidden <- true

                    match cursorClip with
                    | Some lease ->
                        let session =
                            Session(host, mode, input, wake, createdRaw, view, viewport, originalCursor, lease, failed)

                        let startupRevision = InputAccumulator.work_revision input
                        InputAccumulator.discard_pointer_input input
                        PlatformInputWake.acknowledge wake

                        if InputAccumulator.work_pending_since startupRevision input then
                            PlatformInputWake.signal wake

                        Ok session
                    | None -> failwith "The navigation cursor clip was not acquired."
                with error ->
                    match cursorClip with
                    | Some lease -> PlatformCursorClip.release lease |> ignore
                    | None -> ()

                    match raw with
                    | Some created -> PlatformRawInput.stop created |> ignore
                    | None -> ()

                    if cursorHidden then
                        Win32Native.ShowCursor true |> ignore

                    PlatformInputWake.dispose wake
                    Error $"Could not start raw view navigation: {error.Message}"
