module RhinosCanFly.MacMouseDispatch

open System.Diagnostics
open Rhino
open RhinosCanFly.PlatformMouseActions

let dispatch (request: Request) =
    let view = request.host.view

    if not (PlatformInput.viewport_host_exists request.host view) then
        ()
    else
        view.Document.Views.ActiveView <- view

        if
            PlatformInput.viewport_host_is_foreground request.host view
            && pairs[request.button] = request.pair
            && Stopwatch.GetTimestamp() < request.deadline
        then
            match request.action with
            | Flight(mode, held_entry) ->
                let id =
                    MouseFlightEntry.queue
                        request.host
                        mode
                        (if held_entry then Some(fun () -> held request) else None)
                        (if held_entry then 1u <<< request.button else 0u)
                        (Some request.entry_press)
                        request.deadline
                        (fun () -> suspended.Count = 0 && pairs[request.button] = request.pair)

                let macro = $"'_-RhinosCanFlyMouseEntry {id:N}"

                try
                    if not (RhinoApp.RunScript(request.host.document_serial_number, macro, false)) then
                        MouseFlightEntry.cancel id
                with _ ->
                    MouseFlightEntry.cancel id
                    reraise ()
            | Navigate(operation, hold_button) ->
                CurrentConfig.with_loaded (fun (loaded: ConfigLoadResult) ->
                    let target_point =
                        if request.use_cursor then
                            NavigationTargetPoint.ClientPoint request.point
                        else
                            NavigationTargetPoint.ViewCenter

                    match
                        StandaloneNavigation.start
                            view
                            loaded
                            { navigation = Some operation
                              target_point = target_point
                              valid = fun () -> suspended.Count = 0 && pairs[request.button] = request.pair
                              held = if hold_button then Some(fun () -> held request) else None
                              held_buttons = if hold_button then 1u <<< request.button else 0u
                              entry_press = Some request.entry_press
                              mouse_entry = true
                              context = None }
                    with
                    | Ok() -> Rhino.Commands.Result.Success
                    | Error error ->
                        RhinoApp.WriteLine $"RhinosCanFly navigation failed: {error}"
                        Rhino.Commands.Result.Failure)
                |> ignore
            | Retarget mode ->
                match routing with
                | Some config ->
                    let point =
                        if request.use_cursor then
                            request.point
                        else
                            let bounds = view.ActiveViewport.Bounds

                            { x = bounds.Width / 2
                              y = bounds.Height / 2 }

                    let outcome =
                        config.retarget request.host point mode (fun () ->
                            PlatformInput.viewport_host_is_foreground request.host view)

                    for error in outcome.errors do
                        RhinoApp.WriteLine $"Retarget: {error}"
                | None -> ()
