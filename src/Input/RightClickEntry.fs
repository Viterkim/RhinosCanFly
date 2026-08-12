module RhinosCanFly.RightClickEntry

open System
open System.Diagnostics
open Rhino
open Rhino.Commands
open Rhino.Display
open Rhino.UI

type QueuedFlyEntry =
    { view_serial_number: uint32
      document_serial_number: uint32
      root_window: RootWindow
      session_mode: FlightSessionMode
      started_at: int64
      handler: EventHandler }

type RightClickGesture =
    | NoRightClickGesture
    | ViewManipulationClick
    | FlyButtonDown of QueuedFlyEntry
    | FlyButtonReleased of QueuedFlyEntry

type Config =
    { fly_entry_mode: RightClickEntryMode
      default_flight_mode: DefaultFlightMode
      view_manipulation_enabled: bool }

type RightClickCallback() =
    inherit MouseCallback()

    let mutable gesture = NoRightClickGesture
    let mutable flyEntryMode = RightClickEntryMode.Off
    let mutable defaultFlightMode = DefaultFlightMode.Normal
    let mutable suspended = false
    let handlerCleanup = ResizeArray<EventHandler>()

    [<Literal>]
    let queued_entry_timeout_seconds = 2.

    let fly_entry_enabled () =
        match flyEntryMode with
        | RightClickEntryMode.EnterFlying
        | RightClickEntryMode.EnterFlyingDuringCommands
        | RightClickEntryMode.EnterFlyingWhileHeld
        | RightClickEntryMode.EnterFlyingWhileHeldDuringCommands -> true
        | RightClickEntryMode.Off
        | _ -> false

    let enter_during_commands () =
        match flyEntryMode with
        | RightClickEntryMode.EnterFlyingDuringCommands
        | RightClickEntryMode.EnterFlyingWhileHeldDuringCommands -> true
        | RightClickEntryMode.Off
        | RightClickEntryMode.EnterFlying
        | RightClickEntryMode.EnterFlyingWhileHeld
        | _ -> false

    let flight_session_mode () =
        let lifetime =
            match flyEntryMode with
            | RightClickEntryMode.EnterFlyingWhileHeld
            | RightClickEntryMode.EnterFlyingWhileHeldDuringCommands -> FlightLifetime.WhileRightMouseHeld
            | RightClickEntryMode.Off
            | RightClickEntryMode.EnterFlying
            | RightClickEntryMode.EnterFlyingDuringCommands
            | _ -> FlightLifetime.UntilExit

        { lifetime = lifetime
          flight_mode = DefaultFlightMode.flight_mode defaultFlightMode }

    let log_error (context: string) (error: exn) =
        Debug.WriteLine $"RhinosCanFly {context}: {error.Message}"

    let remove_handler (handler: EventHandler) =
        try
            RhinoApp.MainLoop.RemoveHandler handler
            true
        with error ->
            log_error "right-click main-loop cleanup" error
            false

    let retry_handler_cleanup () =
        let mutable index = handlerCleanup.Count - 1

        while index >= 0 do
            if remove_handler handlerCleanup[index] then
                handlerCleanup.RemoveAt index

            index <- index - 1

    let clear_gesture () =
        retry_handler_cleanup ()

        match gesture with
        | FlyButtonDown entry
        | FlyButtonReleased entry ->
            gesture <- NoRightClickGesture

            if not (remove_handler entry.handler) then
                handlerCleanup.Add entry.handler
        | NoRightClickGesture
        | ViewManipulationClick -> gesture <- NoRightClickGesture

    let entry_timed_out (entry: QueuedFlyEntry) =
        let elapsedTicks = Stopwatch.GetTimestamp() - entry.started_at
        float elapsedTicks / float Stopwatch.Frequency >= queued_entry_timeout_seconds

    let recover_from_callback_error (context: string) (event: MouseCallbackEventArgs) (error: exn) =
        try
            clear_gesture ()
        with cleanupError ->
            log_error $"{context} cleanup" cleanupError

        try
            event.Cancel <- false
        with _ ->
            ()

        log_error context error

    let can_enter () =
        enter_during_commands () || not (Command.InCommand())

    let run_fly_entry (entry: QueuedFlyEntry) =
        let queuedView = RhinoView.FromRuntimeSerialNumber entry.view_serial_number

        if isNull queuedView then
            clear_gesture ()
        elif
            isNull queuedView.Document
            || queuedView.Document.RuntimeSerialNumber <> entry.document_serial_number
            || isNull RhinoDoc.ActiveDoc
            || RhinoDoc.ActiveDoc.RuntimeSerialNumber <> entry.document_serial_number
            || isNull queuedView.Document.Views.ActiveView
            || queuedView.Document.Views.ActiveView.RuntimeSerialNumber
               <> entry.view_serial_number
        then
            clear_gesture ()
        elif not (Runtime.viewport_gesture_active queuedView) then
            clear_gesture ()

            if can_enter () && Runtime.can_start () then
                let command =
                    match entry.session_mode.flight_mode with
                    | FlightMode.Normal ->
                        match entry.session_mode.lifetime with
                        | FlightLifetime.UntilExit -> "'_RhinosCanFly"
                        | FlightLifetime.WhileRightMouseHeld -> "'_RhinosCanFlyHeld"
                    | FlightMode.Temporary ->
                        match entry.session_mode.lifetime with
                        | FlightLifetime.UntilExit -> "'_RhinosCanFlyTempFly"
                        | FlightLifetime.WhileRightMouseHeld -> "'_RhinosCanFlyTempFlyHeld"
                    | _ -> "'_RhinosCanFly"

                RhinoApp.RunScript(queuedView.Document.RuntimeSerialNumber, command, false)
                |> ignore

    let queue_fly_entry (view: RhinoView) =
        let handler =
            EventHandler(fun (_: obj) (_: EventArgs) ->
                try
                    retry_handler_cleanup ()

                    if not (fly_entry_enabled ()) then
                        clear_gesture ()

                    match gesture with
                    | FlyButtonDown entry
                    | FlyButtonReleased entry when entry_timed_out entry -> clear_gesture ()
                    | FlyButtonDown entry ->
                        if PlatformInput.foreground_root_window () <> entry.root_window then
                            clear_gesture ()
                        else
                            match entry.session_mode.lifetime with
                            | FlightLifetime.UntilExit ->
                                if not (PlatformInput.right_mouse_button_down ()) then
                                    gesture <- FlyButtonReleased entry
                            | FlightLifetime.WhileRightMouseHeld ->
                                if PlatformInput.right_mouse_button_down () then
                                    run_fly_entry entry
                                else
                                    clear_gesture ()
                    | FlyButtonReleased entry ->
                        if PlatformInput.foreground_root_window () <> entry.root_window then
                            clear_gesture ()
                        else
                            run_fly_entry entry
                    | NoRightClickGesture
                    | ViewManipulationClick -> ()
                with error ->
                    try
                        clear_gesture ()
                    with cleanupError ->
                        log_error "right-click main-loop cleanup" cleanupError

                    log_error "right-click main-loop handler" error)

        gesture <-
            FlyButtonDown
                { view_serial_number = view.RuntimeSerialNumber
                  document_serial_number = view.Document.RuntimeSerialNumber
                  root_window = PlatformInput.root_window view
                  session_mode = flight_session_mode ()
                  started_at = Stopwatch.GetTimestamp()
                  handler = handler }

        try
            RhinoApp.MainLoop.AddHandler handler
        with error ->
            gesture <- NoRightClickGesture
            raise error

    override _.OnMouseDown(event: MouseCallbackEventArgs) =
        try
            let isRightButton = event.MouseButton = MouseButton.Right

            if isRightButton then
                clear_gesture ()

            let isPerspective =
                not (isNull event.View) && event.View.ActiveViewport.IsPerspectiveProjection

            let mutable viewManipulationHandled = false

            if isRightButton && not (isNull event.View) && not (Runtime.is_running ()) then
                match PlatformInput.handle_view_manipulation_right_click event.View with
                | Ok true ->
                    event.Cancel <- true
                    gesture <- ViewManipulationClick
                    viewManipulationHandled <- true
                | Ok false -> ()
                | Error error ->
                    event.Cancel <- true
                    gesture <- ViewManipulationClick
                    viewManipulationHandled <- true
                    RhinoApp.WriteLine $"RhinosCanFly view manipulation error: {error}"

            if
                not viewManipulationHandled
                && fly_entry_enabled ()
                && isRightButton
                && isPerspective
                && can_enter ()
                && Runtime.can_start ()
            then
                event.Cancel <- true

                match gesture with
                | NoRightClickGesture -> queue_fly_entry event.View
                | ViewManipulationClick
                | FlyButtonDown _
                | FlyButtonReleased _ -> ()
        with error ->
            recover_from_callback_error "right-click mouse-down callback" event error

    override _.OnMouseUp(event: MouseCallbackEventArgs) =
        try
            if event.MouseButton = MouseButton.Right then
                match gesture with
                | ViewManipulationClick ->
                    gesture <- NoRightClickGesture
                    event.Cancel <- true
                | FlyButtonDown entry ->
                    match entry.session_mode.lifetime with
                    | FlightLifetime.UntilExit -> gesture <- FlyButtonReleased entry
                    | FlightLifetime.WhileRightMouseHeld -> clear_gesture ()

                    event.Cancel <- true
                | FlyButtonReleased _ -> event.Cancel <- true
                | NoRightClickGesture -> ()
        with error ->
            recover_from_callback_error "right-click mouse-up callback" event error

    member this.Configure(config: Config) =
        clear_gesture ()
        flyEntryMode <- config.fly_entry_mode
        defaultFlightMode <- config.default_flight_mode

        this.Enabled <- not suspended && (fly_entry_enabled () || config.view_manipulation_enabled)

    member this.Suspend() =
        clear_gesture ()
        this.Enabled <- false
        suspended <- true

    member this.Resume() =
        this.Enabled <- fly_entry_enabled () || PlatformInput.mouse_button_right_click_enabled ()
        suspended <- false

    member this.DiagnosticLine() =
        let description =
            match gesture with
            | NoRightClickGesture -> "none"
            | ViewManipulationClick -> "view manipulation"
            | FlyButtonDown entry
            | FlyButtonReleased entry ->
                let age =
                    float (Stopwatch.GetTimestamp() - entry.started_at) / float Stopwatch.Frequency

                $"fly entry age={age:F3}s; document={entry.document_serial_number}; view={entry.view_serial_number}"

        $"Right click: enabled={this.Enabled}; suspended={suspended}; gesture={description}; retained handlers={handlerCleanup.Count}"

    member this.Shutdown() =
        try
            this.Configure
                { fly_entry_mode = RightClickEntryMode.Off
                  default_flight_mode = DefaultFlightMode.Normal
                  view_manipulation_enabled = false }
        finally
            retry_handler_cleanup ()

let callback = RightClickCallback()

let configure (config: Config) = callback.Configure config

let suspend () = callback.Suspend()

let resume () = callback.Resume()

let diagnostic_line () = callback.DiagnosticLine()

let shutdown () = callback.Shutdown()
