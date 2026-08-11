module RhinosCanFly.RightClickEntry

open System
open System.Diagnostics
open Rhino
open Rhino.Commands
open Rhino.Display
open Rhino.UI

type QueuedFlyEntry =
    { view_serial_number: uint32
      root_window: RootWindow
      session_mode: FlightSessionMode
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

    let clear_gesture () =
        match gesture with
        | FlyButtonDown entry
        | FlyButtonReleased entry ->
            RhinoApp.MainLoop.RemoveHandler entry.handler
            gesture <- NoRightClickGesture
        | NoRightClickGesture
        | ViewManipulationClick -> gesture <- NoRightClickGesture

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
                    if not (fly_entry_enabled ()) then
                        clear_gesture ()

                    match gesture with
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
                  root_window = PlatformInput.root_window view
                  session_mode = flight_session_mode ()
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
        flyEntryMode <- config.fly_entry_mode
        defaultFlightMode <- config.default_flight_mode

        if not (fly_entry_enabled ()) then
            clear_gesture ()

        this.Enabled <- fly_entry_enabled () || config.view_manipulation_enabled

    member this.Shutdown() =
        this.Configure
            { fly_entry_mode = RightClickEntryMode.Off
              default_flight_mode = DefaultFlightMode.Normal
              view_manipulation_enabled = false }

let callback = RightClickCallback()

let configure (config: Config) = callback.Configure config

let shutdown () = callback.Shutdown()
