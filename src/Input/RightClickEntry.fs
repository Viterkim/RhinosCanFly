module RhinosCanFly.RightClickEntry

open System
open Rhino
open Rhino.Commands
open Rhino.UI

type RightClickCallback() =
    inherit MouseCallback()

    let mutable queued = false

    override _.OnMouseDown(event: MouseCallbackEventArgs) =
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
                let mutable idleHandler = Unchecked.defaultof<EventHandler>

                idleHandler <-
                    EventHandler(fun (_: obj) (_: EventArgs) ->
                        RhinoApp.Idle.RemoveHandler idleHandler
                        queued <- false

                        if not (Command.InCommand()) && not (Runtime.is_running ()) then
                            RhinoApp.RunScript("! _RhinosCanFly", false) |> ignore)

                RhinoApp.Idle.AddHandler idleHandler

let callback = RightClickCallback()

let set_enabled (enabled: bool) = callback.Enabled <- enabled

let initialize_from_config () =
    match Config.load () with
    | Ok loaded ->
        set_enabled loaded.config.hijack_right_click_to_enter
        Ok()
    | Error error -> Error error
