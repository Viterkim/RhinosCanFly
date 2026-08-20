module RhinosCanFly.NavigationTarget

open Rhino
open Rhino.Display

let apply (loaded: ConfigLoadResult) (view: RhinoView) =
    try
        if isNull view || isNull view.Document then
            Error "The navigation viewport is unavailable."
        else
            let behavior = loaded.config.behavior

            if behavior.view_target.mode <> ViewTargetMode.Off then
                let movement = loaded.config.movement

                let speed =
                    FlightSpeed.current
                        view.Document
                        behavior.load_speed_from_document
                        movement.speed_range
                        movement.base_speed

                ViewTarget.apply behavior.view_target speed view.ActiveViewport

            Ok()
    with error ->
        Error $"Could not set the navigation target: {error.Message}"

let prepare (loaded: ConfigLoadResult) (host: ViewportHostIdentity) (_: ViewNavigationMode) =
    try
        let view = RhinoView.FromRuntimeSerialNumber host.view_serial_number

        if isNull view || isNull view.Document then
            Error "The navigation viewport is unavailable."
        elif PlatformInput.foreground_root_window () <> host.root_window then
            Error "The navigation viewport is no longer active."
        else
            let document = view.Document
            let activeDocument = RhinoDoc.ActiveDoc

            if
                isNull activeDocument
                || activeDocument.RuntimeSerialNumber <> host.document_serial_number
            then
                Error "The navigation document is no longer active."
            else
                let activeView = document.Views.ActiveView

                if isNull activeView || activeView.RuntimeSerialNumber <> host.view_serial_number then
                    document.Views.ActiveView <- view

                let currentHost = PlatformInput.capture_viewport_host view

                if
                    currentHost.document_serial_number = host.document_serial_number
                    && currentHost.view_serial_number = host.view_serial_number
                    && currentHost.view_window = host.view_window
                    && currentHost.root_window = host.root_window
                then
                    match apply loaded view with
                    | Ok() -> Ok currentHost
                    | Error error -> Error error
                else
                    Error "The navigation viewport changed before it could be activated."
    with error ->
        Error $"Could not prepare view navigation: {error.Message}"
