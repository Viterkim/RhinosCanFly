module RhinosCanFly.NavigationTarget

// Rhino 9 deprecates GetViewList(bool, bool), but Rhino 7 has no replacement.
#nowarn "44"

open Rhino
open Rhino.Display

let apply (loaded: ConfigLoadResult) (mode: ViewNavigationMode) (view: RhinoView) =
    try
        if isNull view || isNull view.Document then
            Error "The navigation viewport is unavailable."
        else
            let behavior = loaded.config.behavior

            let retargetMode =
                match mode with
                | ViewNavigationMode.Pivot -> behavior.retarget.on_pivot
                | ViewNavigationMode.Pan -> behavior.retarget.on_pan

            if retargetMode <> RetargetMode.Off then
                let movement = loaded.config.movement

                let speed =
                    FlightSpeed.current
                        view.Document
                        behavior.load_speed_from_document
                        movement.speed_range
                        movement.base_speed

                ViewTarget.apply behavior.retarget retargetMode speed view view.ActiveViewport

            Ok()
    with error ->
        Error $"Could not set the navigation target: {error.Message}"

let prepare (loaded: ConfigLoadResult) (host: ViewportHostIdentity) (mode: ViewNavigationMode) =
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
                    && currentHost.viewport_id = host.viewport_id
                    && currentHost.view_window = host.view_window
                    && currentHost.root_window = host.root_window
                then
                    match apply loaded mode view with
                    | Ok() -> Ok currentHost
                    | Error error -> Error error
                else
                    Error "The navigation viewport changed before it could be activated."
    with error ->
        Error $"Could not prepare view navigation: {error.Message}"

let move_view_to_target (retarget: RetargetConfig) (speed: float) (target: Rhino.Geometry.Point3d) (view: RhinoView) =
    if not (isNull view) then
        let viewport = view.ActiveViewport
        let mutable direction = viewport.CameraDirection
        let up = viewport.CameraY

        match ViewTarget.retarget_distance retarget speed viewport with
        | Some distance when direction.Unitize() ->
            let magnification =
                ViewTarget.parallel_magnification_to_distance viewport target distance

            viewport.SetCameraLocations(target, target - direction * distance)
            viewport.CameraUp <- up

            if magnification <> 1. then
                viewport.Magnify(magnification, true) |> ignore

            view.Redraw()
        | Some _
        | None -> ()

let retarget
    (loaded: ConfigLoadResult)
    (host: ViewportHostIdentity)
    (clientPoint: ViewportClientPoint)
    (mode: RetargetMode)
    =
    try
        let view = RhinoView.FromRuntimeSerialNumber host.view_serial_number

        if isNull view || isNull view.Document then
            Error "The retarget viewport is unavailable."
        elif
            not (PlatformInput.viewport_host_exists host view)
            || not (PlatformInput.viewport_id_matches host view)
            || PlatformInput.foreground_root_window () <> host.root_window
        then
            Error "The retarget viewport is no longer active."
        else
            let document = view.Document
            let activeDocument = RhinoDoc.ActiveDoc

            if
                isNull activeDocument
                || activeDocument.RuntimeSerialNumber <> host.document_serial_number
            then
                Error "The retarget document is no longer active."
            else
                let activeView = document.Views.ActiveView

                if isNull activeView || activeView.RuntimeSerialNumber <> view.RuntimeSerialNumber then
                    document.Views.ActiveView <- view

                let viewport = view.ActiveViewport
                let behavior = loaded.config.behavior
                let movement = loaded.config.movement

                let speed =
                    FlightSpeed.current
                        document
                        behavior.load_speed_from_document
                        movement.speed_range
                        movement.base_speed

                match ViewTarget.selected_target_at behavior.retarget mode speed view viewport clientPoint with
                | None -> Ok()
                | Some target ->
                    move_view_to_target behavior.retarget speed target view

                    for other in document.Views.GetViewList(true, false) do
                        if not (isNull other) && other.RuntimeSerialNumber <> view.RuntimeSerialNumber then
                            move_view_to_target behavior.retarget speed target other

                    Ok()
    with error ->
        Error $"Could not retarget the viewports: {error.Message}"
