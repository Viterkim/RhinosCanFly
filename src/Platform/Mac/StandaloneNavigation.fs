module RhinosCanFly.StandaloneNavigation

open Rhino
open Rhino.Commands

let navigation_config (config: FlyConfig) =
    let empty = PlatformBindings.empty

    { config with
        bindings =
            { config.bindings with
                forward = empty
                backward = empty
                left = empty
                right = empty
                up = empty
                down = empty
                key_pivot_left = empty
                key_pivot_right = empty
                mouse_navigation =
                    { pivot = { toggle = None; hold = None }
                      pan = { toggle = None; hold = None } } }
        behavior =
            { config.behavior with
                hide_gumball = false
                crosshair =
                    { config.behavior.crosshair with
                        enabled = false } } }

let start (view: Rhino.Display.RhinoView) (loaded: ConfigLoadResult) (entry: FlightSession.Entry) =
    FlightSession.finish_pending_exit ()

    let flight_mode =
        if DefaultFlightMode.restores_navigation_commands loaded.config_file.default_flight_mode then
            FlightMode.Temporary
        else
            FlightMode.Normal

    let toggled_off =
        match FlightSession.current with
        | Some session ->
            let same =
                session.entry.navigation = entry.navigation
                && obj.ReferenceEquals(session.state.view, view)

            FlightSession.stop ExplicitKeepCamera
            same
        | None -> false

    if toggled_off then
        Ok()
    else
        let requested_entry = entry

        let entry =
            { requested_entry with
                valid =
                    fun () ->
                        requested_entry.valid ()
                        && not (RuntimeSettings.input_suspended ())
                        && RuntimeSettings.runtime_enabled () }

        FlightSession.run_session
            view
            (navigation_config loaded.config)
            (FlightSessionMode.until_exit flight_mode)
            entry

let run (mode: ViewNavigationMode) (document: RhinoDoc) =
    PlatformFlightKeyboard.prepare_entry false
    let struct (mouse, context) = PlatformFlightKeyboard.take_entry ()

    if RuntimeSettings.input_suspended () || not (RuntimeSettings.runtime_enabled ()) then
        Result.Cancel
    elif isNull document.Views.ActiveView then
        Result.Failure
    else
        CurrentConfig.with_loaded (fun (loaded: ConfigLoadResult) ->
            let view = document.Views.ActiveView

            let operation =
                if mode = ViewNavigationMode.Pivot then
                    ViewportNavigation.Operation.Pivot
                else
                    ViewportNavigation.Operation.Pan

            FlightSession.finish_pending_exit ()

            let position = Platform.Mac.MacNative.cursor_position ()

            let point =
                view.ActiveViewport.ScreenToClient(System.Drawing.Point(int position.x, int position.y))

            let bounds = view.ActiveViewport.Bounds

            let toggling_off =
                FlightSession.current
                |> Option.exists (fun (session: FlightSession.Session) ->
                    session.entry.navigation = Some operation
                    && obj.ReferenceEquals(session.state.view, view))

            if
                not toggling_off
                && (point.X < 0
                    || point.Y < 0
                    || point.X >= bounds.Width
                    || point.Y >= bounds.Height)
            then
                RhinoApp.WriteLine "Move the cursor over the active viewport."
                Result.Cancel
            else
                match
                    start
                        view
                        loaded
                        { navigation = Some operation
                          target_point = NavigationTargetPoint.ClientPoint { x = int point.X; y = int point.Y }
                          valid = fun () -> true
                          held = None
                          held_buttons = 0u
                          entry_press = None
                          mouse_entry = mouse
                          context = context }
                with
                | Ok() -> Result.Success
                | Error error ->
                    RhinoApp.WriteLine $"RhinosCanFly navigation failed: {error}"
                    Result.Failure)
