module RhinosCanFly.RightClickEntry

open System
open System.Diagnostics
open Rhino
open Rhino.Commands
open Rhino.Display
open Rhino.UI

type QueuedFlyEntry =
    { view_serial_number: uint32
      root_window: nativeint
      handler: EventHandler }

type RightClickGesture =
    | NoRightClickGesture
    | ViewManipulationClick
    | FlyButtonDown of QueuedFlyEntry
    | FlyButtonReleased of QueuedFlyEntry

type RightClickCallback() =
    inherit MouseCallback()

    let mutable gesture = NoRightClickGesture
    let mutable flyEnabled = false
    let mutable hijackDuringCommands = false

    let log_error (context: string) (error: exn) =
        Debug.WriteLine $"RhinosCanFly {context}: {error.Message}"

    let clear_gesture () =
        let previous = gesture
        gesture <- NoRightClickGesture

        match previous with
        | FlyButtonDown entry
        | FlyButtonReleased entry -> RhinoApp.MainLoop.RemoveHandler entry.handler
        | NoRightClickGesture
        | ViewManipulationClick -> ()

    let can_enter () =
        hijackDuringCommands || not (Command.InCommand())

    let queue_fly_entry (view: RhinoView) =
        let handler =
            EventHandler(fun (_: obj) (_: EventArgs) ->
                try
                    match gesture with
                    | FlyButtonDown entry ->
                        if PlatformInput.foreground_window () <> entry.root_window then
                            clear_gesture ()
                        elif not (PlatformInput.right_mouse_button_down ()) then
                            gesture <- FlyButtonReleased entry
                    | FlyButtonReleased entry ->
                        if PlatformInput.foreground_window () <> entry.root_window then
                            clear_gesture ()
                        else
                            let queuedView = RhinoView.FromRuntimeSerialNumber entry.view_serial_number

                            if isNull queuedView then
                                clear_gesture ()
                            elif not (Runtime.viewport_gesture_active queuedView) then
                                clear_gesture ()

                                if can_enter () && Runtime.can_start () then
                                    if not (RhinoApp.RunScript("'_RhinosCanFly", false)) then
                                        RhinoApp.WriteLine "RhinosCanFly could not start during the current command."
                    | NoRightClickGesture
                    | ViewManipulationClick -> ()
                with error ->
                    clear_gesture ()
                    log_error "right-click main-loop handler" error)

        gesture <-
            FlyButtonDown
                { view_serial_number = view.RuntimeSerialNumber
                  root_window = PlatformInput.root_window view.Handle
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
                match PlatformInput.handle_view_manipulation_right_click event.View.Handle with
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
                && flyEnabled
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
            clear_gesture ()

            try
                event.Cancel <- false
            with _ ->
                ()

            log_error "right-click callback" error

    override _.OnMouseUp(event: MouseCallbackEventArgs) =
        if event.MouseButton = MouseButton.Right then
            match gesture with
            | ViewManipulationClick ->
                gesture <- NoRightClickGesture
                event.Cancel <- true
            | FlyButtonDown entry ->
                gesture <- FlyButtonReleased entry
                event.Cancel <- true
            | FlyButtonReleased _ -> event.Cancel <- true
            | NoRightClickGesture -> ()

    member this.Configure(flyEntryEnabled: bool, duringCommands: bool, viewManipulationEnabled: bool) =
        if not flyEntryEnabled then
            clear_gesture ()

        flyEnabled <- flyEntryEnabled
        hijackDuringCommands <- duringCommands
        this.Enabled <- flyEntryEnabled || viewManipulationEnabled

    member this.Shutdown() = this.Configure(false, false, false)

let callback = RightClickCallback()

let configure (flyEntryEnabled: bool) (duringCommands: bool) (viewManipulationEnabled: bool) =
    callback.Configure(flyEntryEnabled, duringCommands, viewManipulationEnabled)

let shutdown () = callback.Shutdown()
