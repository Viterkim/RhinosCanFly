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

    let log_error (context: string) (error: exn) =
        Debug.WriteLine $"RhinosCanFly {context}: {error.Message}"

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
                    let mutable idleHandler = Unchecked.defaultof<EventHandler>

                    idleHandler <-
                        EventHandler(fun (_: obj) (_: EventArgs) ->
                            try
                                let view = RhinoView.FromRuntimeSerialNumber viewSerialNumber

                                if isNull view then
                                    RhinoApp.Idle.RemoveHandler idleHandler
                                    queued <- false
                                elif not (view.MouseCaptured(false)) then
                                    RhinoApp.Idle.RemoveHandler idleHandler
                                    queued <- false

                                    if not (Command.InCommand()) && not (Runtime.is_running ()) then
                                        RhinoApp.RunScript("! _RhinosCanFly", false) |> ignore
                            with error ->
                                RhinoApp.Idle.RemoveHandler idleHandler
                                queued <- false
                                log_error "right-click idle handler" error)

                    RhinoApp.Idle.AddHandler idleHandler
        with error ->
            queued <- false

            try
                event.Cancel <- false
            with _ ->
                ()

            log_error "right-click callback" error

    override _.OnMouseUp(event: MouseCallbackEventArgs) =
        if queued && event.MouseButton = MouseButton.Right then
            event.Cancel <- true

let callback = RightClickCallback()

let set_enabled (enabled: bool) = callback.Enabled <- enabled
