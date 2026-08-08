module RhinosCanFly.RightClickEntry

open System
open System.Diagnostics
open Rhino
open Rhino.Commands
open Rhino.Display
open Rhino.UI

type RightClickCallback() =
    inherit MouseCallback()

    let mutable queued = false
    let mutable rightButtonReleased = false
    let mutable mainLoopHandler: EventHandler option = None
    let mutable flyEnabled = false
    let mutable hijackDuringCommands = false
    let mutable viewManipulationClick = false

    let log_error (context: string) (error: exn) =
        Debug.WriteLine $"RhinosCanFly {context}: {error.Message}"

    let clear_queue () =
        match mainLoopHandler with
        | Some handler ->
            RhinoApp.MainLoop.RemoveHandler handler
            mainLoopHandler <- None
        | None -> ()

        queued <- false
        rightButtonReleased <- false

    let can_enter () =
        hijackDuringCommands || not (Command.InCommand())

    override _.OnMouseDown(event: MouseCallbackEventArgs) =
        try
            let isRightButton = event.MouseButton = MouseButton.Right

            let isPerspective =
                not (isNull event.View) && event.View.ActiveViewport.IsPerspectiveProjection

            let mutable viewManipulationHandled = false

            if isRightButton && not (isNull event.View) && not (Runtime.is_running ()) then
                match PlatformInput.handle_view_manipulation_right_click event.View.Handle with
                | Ok true ->
                    event.Cancel <- true
                    viewManipulationClick <- true
                    viewManipulationHandled <- true
                | Ok false -> ()
                | Error error ->
                    event.Cancel <- true
                    viewManipulationClick <- true
                    viewManipulationHandled <- true
                    RhinoApp.WriteLine $"RhinosCanFly view manipulation error: {error}"

            if
                not viewManipulationHandled
                && flyEnabled
                && isRightButton
                && isPerspective
                && can_enter ()
                && not (Runtime.is_running ())
            then
                event.Cancel <- true

                if not queued then
                    queued <- true
                    rightButtonReleased <- false
                    let viewSerialNumber = event.View.RuntimeSerialNumber

                    let handler =
                        EventHandler(fun (_: obj) (_: EventArgs) ->
                            try
                                let view = RhinoView.FromRuntimeSerialNumber viewSerialNumber

                                if isNull view then
                                    clear_queue ()
                                elif rightButtonReleased && not (Runtime.viewport_gesture_active view) then
                                    clear_queue ()

                                    if can_enter () && not (Runtime.is_running ()) then
                                        if not (RhinoApp.RunScript("'_RhinosCanFly", false)) then
                                            RhinoApp.WriteLine
                                                "RhinosCanFly could not start during the current command."
                            with error ->
                                clear_queue ()
                                log_error "right-click idle handler" error)

                    mainLoopHandler <- Some handler
                    RhinoApp.MainLoop.AddHandler handler
        with error ->
            clear_queue ()

            try
                event.Cancel <- false
            with _ ->
                ()

            log_error "right-click callback" error

    override _.OnMouseUp(event: MouseCallbackEventArgs) =
        if viewManipulationClick && event.MouseButton = MouseButton.Right then
            viewManipulationClick <- false
            event.Cancel <- true
        elif queued && event.MouseButton = MouseButton.Right then
            rightButtonReleased <- true
            event.Cancel <- true

    member this.Configure(flyEntryEnabled: bool, duringCommands: bool, viewManipulationEnabled: bool) =
        if not flyEntryEnabled then
            clear_queue ()

        flyEnabled <- flyEntryEnabled
        hijackDuringCommands <- duringCommands
        this.Enabled <- flyEntryEnabled || viewManipulationEnabled

    member this.Shutdown() = this.Configure(false, false, false)

let callback = RightClickCallback()

let configure (flyEntryEnabled: bool) (duringCommands: bool) (viewManipulationEnabled: bool) =
    callback.Configure(flyEntryEnabled, duringCommands, viewManipulationEnabled)

let shutdown () = callback.Shutdown()
