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
        let (ViewWindowHandle expected_window) = host.view_window
        let root = Win32Native.GetAncestor(view.Handle, Win32Native.GA_ROOT)
        let actual_root = if root = nativeint 0 then view.Handle else root
        let (RootWindow expected_root) = host.root_window

        view.RuntimeSerialNumber = host.view_serial_number
        && view.Document.RuntimeSerialNumber = host.document_serial_number
        && view.ActiveViewportID = host.viewport_id
        && view.Handle = expected_window
        && actual_root = expected_root

[<Struct>]
type DrainResult = { count: int; overflowed: bool }

type Session
    internal
    (
        host: ViewportHostIdentity,
        input: InputAccumulator.State,
        wake: PlatformInputWake.State,
        raw: PlatformRawInput.Session,
        view: RhinoView,
        viewport: RhinoViewport,
        original_cursor: System.Drawing.Point,
        cursor_clip: CursorClipLease,
        failed: Action
    ) =

    let mutable active = true
    let mutable draining = false
    let mutable failure_notified = false
    let mutable raw_input_clean = false
    let mutable clip_released = false
    let mutable cursor_restored = false
    let mutable cursor_hidden = true
    let mutable wake_disposed = false

    member _.NotifyFailure() =
        if active && not failure_notified then
            failure_notified <- true
            failed.Invoke()

    member _.Host = host
    member _.View = view
    member _.Viewport = viewport
    member _.OriginalCursor = original_cursor
    member _.IsActive = active
    member _.WorkPending = InputAccumulator.work_pending input

    member _.CleanupComplete =
        not active
        && raw_input_clean
        && clip_released
        && cursor_restored
        && not cursor_hidden
        && wake_disposed

    member _.RawInputRegistrationIsCurrent() =
        PlatformRawInput.registration_is_current raw

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
            let observed_revision = InputAccumulator.work_revision input

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
                    && not failure_notified
                    && InputAccumulator.work_pending_since observed_revision input
                then
                    PlatformInputWake.signal wake

                draining <- false

    member _.Stop() =
        active <- false
        let errors = ResizeArray<string>()

        let attempt (name: string) (action: unit -> unit) =
            try
                action ()
            with error ->
                errors.Add $"{name}: {error.Message}"

        if not raw_input_clean then
            attempt "raw-input stop request" (fun () ->
                match PlatformRawInput.request_stop raw with
                | Ok() -> ()
                | Error error -> errors.Add $"raw-input stop request: {error}")

        if not clip_released then
            attempt "cursor clip" (fun () ->
                match PlatformCursorClip.release cursor_clip with
                | Ok() -> clip_released <- true
                | Error error -> errors.Add $"cursor clip: {error}")

        if not raw_input_clean then
            attempt "raw input" (fun () ->
                let outcome = PlatformRawInput.stop raw

                raw_input_clean <-
                    outcome.terminated
                    && outcome.registration_relinquished
                    && not outcome.previous_registration_lost

                if not raw_input_clean then
                    errors.Add "raw input did not shut down cleanly"

                for error in outcome.errors do
                    errors.Add $"raw input: {error}")

        if not cursor_restored then
            attempt "cursor position" (fun () ->
                let (RootWindow root_window) = host.root_window

                if
                    root_window <> nativeint 0
                    && Win32Native.IsWindow root_window
                    && Win32Native.GetForegroundWindow() = root_window
                then
                    match Win32.set_cursor_position original_cursor with
                    | Ok() -> cursor_restored <- true
                    | Error error -> errors.Add $"cursor position: {error}"
                else
                    cursor_restored <- true)

        if cursor_hidden then
            attempt "cursor visibility" (fun () ->
                Win32Native.ShowCursor true |> ignore
                cursor_hidden <- false)

        if not wake_disposed then
            attempt "input wake" (fun () ->
                PlatformInputWake.dispose wake
                wake_disposed <- true)

        if errors.Count = 0 then
            Ok()
        else
            Error(String.Join("; ", errors))

let start (host: ViewportHostIdentity) (mode: ViewportNavigation.Operation) (admit: unit -> bool) (failed: Action) =
    let view = RhinoView.FromRuntimeSerialNumber host.view_serial_number

    if not (view_matches_host host view) then
        Error "The navigation viewport is unavailable."
    else
        let viewport = view.ActiveViewport

        let requires_parallel_projection =
            mode = ViewportNavigation.Operation.ParallelPan
            || mode = ViewportNavigation.Operation.ParallelZoom

        if requires_parallel_projection && not viewport.IsParallelProjection then
            Error "The viewport is no longer using parallel projection."
        else
            match Win32.get_cursor_position () with
            | Error error -> Error error
            | Ok original_cursor ->
                let input = InputAccumulator.create ()
                let wake = PlatformInputWake.create host.root_window
                let input_available = Action(fun () -> PlatformInputWake.signal wake)
                let mutable raw: PlatformRawInput.Session option = None
                let mutable cursor_clip: CursorClipLease option = None
                let mutable cursor_hidden = false

                try
                    if Win32Native.GetCapture() <> nativeint 0 || not (admit ()) then
                        failwith "Rhino owns an unfinished mouse interaction."

                    let created_raw =
                        PlatformRawInput.start
                            (Win32.mouse_buttons_swapped ())
                            (fun () ->
                                Win32Native.GetCapture() = nativeint 0
                                && PlatformInput.viewport_host_is_foreground host view
                                && admit ())
                            input
                            input_available

                    raw <- Some created_raw

                    match PlatformCursorClip.acquire view with
                    | Error error -> failwith error
                    | Ok lease -> cursor_clip <- Some lease

                    Win32Native.ShowCursor false |> ignore
                    cursor_hidden <- true

                    match cursor_clip with
                    | Some lease ->
                        let session =
                            Session(host, input, wake, created_raw, view, viewport, original_cursor, lease, failed)

                        let startup_revision = InputAccumulator.work_revision input
                        InputAccumulator.discard_pointer_input input
                        PlatformInputWake.acknowledge wake

                        if InputAccumulator.work_pending_since startup_revision input then
                            PlatformInputWake.signal wake

                        Ok session
                    | None -> failwith "The navigation cursor clip was not acquired."
                with error ->
                    let errors = ResizeArray<string>()
                    errors.Add $"Could not start raw view navigation: {error.Message}"

                    let attempt (name: string) (action: unit -> unit) =
                        try
                            action ()
                        with cleanup_error ->
                            errors.Add $"{name}: {cleanup_error.Message}"

                    match cursor_clip with
                    | Some lease ->
                        attempt "cursor clip" (fun () ->
                            match PlatformCursorClip.release lease with
                            | Ok() -> ()
                            | Error message -> errors.Add message)
                    | None -> ()

                    match raw with
                    | Some created ->
                        attempt "raw input" (fun () ->
                            let outcome = PlatformRawInput.stop created

                            for message in outcome.errors do
                                errors.Add message)
                    | None -> ()

                    if cursor_hidden then
                        attempt "cursor visibility" (fun () -> Win32Native.ShowCursor true |> ignore)

                    attempt "input wake" (fun () -> PlatformInputWake.dispose wake)
                    Error(String.concat "; " errors)
