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
    let mutable hijackDuringCommands = false

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
            let isPerspective =
                not (isNull event.View) && event.View.ActiveViewport.IsPerspectiveProjection

            if
                event.MouseButton = MouseButton.Right
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
        if queued && event.MouseButton = MouseButton.Right then
            rightButtonReleased <- true
            event.Cancel <- true

    member this.Configure(enabled: bool, duringCommands: bool) =
        if not enabled then
            clear_queue ()

        hijackDuringCommands <- duringCommands
        this.Enabled <- enabled

    member this.Shutdown() = this.Configure(false, false)

let callback = RightClickCallback()

let configure (enabled: bool) (duringCommands: bool) =
    callback.Configure(enabled, duringCommands)

let shutdown () = callback.Shutdown()
