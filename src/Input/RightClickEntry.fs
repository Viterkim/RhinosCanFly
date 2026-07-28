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
    let mutable idleHandler: EventHandler option = None

    let log_error (context: string) (error: exn) =
        Debug.WriteLine $"RhinosCanFly {context}: {error.Message}"

    let clear_queue () =
        match idleHandler with
        | Some handler ->
            RhinoApp.Idle.RemoveHandler handler
            idleHandler <- None
        | None -> ()

        queued <- false

    override _.OnMouseDown(event: MouseCallbackEventArgs) =
        try
            let isPerspective =
                not (isNull event.View) && event.View.ActiveViewport.IsPerspectiveProjection

            if
                event.MouseButton = MouseButton.Right
                && isPerspective
                && not (Command.InCommand())
                && not (Runtime.is_running ())
            then
                event.Cancel <- true

                if not queued then
                    queued <- true
                    let viewSerialNumber = event.View.RuntimeSerialNumber

                    let handler =
                        EventHandler(fun (_: obj) (_: EventArgs) ->
                            try
                                let view = RhinoView.FromRuntimeSerialNumber viewSerialNumber

                                if isNull view then
                                    clear_queue ()
                                elif not (Runtime.viewport_gesture_active view) then
                                    clear_queue ()

                                    if not (Command.InCommand()) && not (Runtime.is_running ()) then
                                        RhinoApp.RunScript("! _RhinosCanFly", false) |> ignore
                            with error ->
                                clear_queue ()
                                log_error "right-click idle handler" error)

                    idleHandler <- Some handler
                    RhinoApp.Idle.AddHandler handler
        with error ->
            clear_queue ()

            try
                event.Cancel <- false
            with _ ->
                ()

            log_error "right-click callback" error

    override _.OnMouseUp(event: MouseCallbackEventArgs) =
        if queued && event.MouseButton = MouseButton.Right then
            event.Cancel <- true

    member this.SetEnabled(enabled: bool) =
        if not enabled then
            clear_queue ()

        this.Enabled <- enabled

    member this.Shutdown() = this.SetEnabled false

let callback = RightClickCallback()

let set_enabled (enabled: bool) = callback.SetEnabled enabled

let shutdown () = callback.Shutdown()
