module RhinosCanFly.NavigationTarget

// Rhino 9 deprecates GetViewList(bool, bool), but Rhino 7 has no replacement.
#nowarn "44"

open Rhino
open Rhino.ApplicationSettings
open Rhino.Display
open Rhino.Geometry

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

                ViewTarget.apply_for_navigation behavior.retarget retargetMode mode speed view view.ActiveViewport

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

            if magnification = 1. || viewport.Magnify(magnification, true) then
                viewport.SetCameraLocations(target, target - direction * distance)
                viewport.CameraUp <- up
                view.Redraw()
        | Some _
        | None -> ()

let zoom_view_to_selection (target: Point3d) (bounds: BoundingBox) (view: RhinoView) =
    if not (isNull view) then
        let viewport = view.ActiveViewport
        let up = viewport.CameraY

        if viewport.ZoomBoundingBox bounds then
            let offset = target - viewport.CameraTarget
            viewport.SetCameraLocations(target, viewport.CameraLocation + offset)
            viewport.CameraUp <- up
            view.Redraw()

let zoom_views_to_selection (retarget: RetargetConfig) (target: Point3d) (bounds: BoundingBox) (view: RhinoView) =
    let previousPerspectiveBorder = ViewSettings.ZoomExtentsPerspectiveViewBorder
    let previousParallelBorder = ViewSettings.ZoomExtentsParallelViewBorder

    try
        ViewSettings.ZoomExtentsPerspectiveViewBorder <- retarget.perspective_zoom_border
        ViewSettings.ZoomExtentsParallelViewBorder <- retarget.parallel_zoom_border

        zoom_view_to_selection target bounds view

        for other in view.Document.Views.GetViewList(true, false) do
            if not (isNull other) && other.RuntimeSerialNumber <> view.RuntimeSerialNumber then
                zoom_view_to_selection target bounds other
    finally
        ViewSettings.ZoomExtentsPerspectiveViewBorder <- previousPerspectiveBorder
        ViewSettings.ZoomExtentsParallelViewBorder <- previousParallelBorder

let set_view_target (target: Point3d) (view: RhinoView) =
    if not (isNull view) then
        view.ActiveViewport.SetCameraTarget(target, false)
        view.Redraw()

let apply_selection
    (retarget: RetargetConfig)
    (mode: RetargetMode)
    (speed: float)
    (selection: ViewTarget.RetargetSelection)
    (view: RhinoView)
    =
    match selection.bounds with
    | ValueSome bounds -> zoom_views_to_selection retarget selection.target bounds view
    | ValueNone when RetargetMode.uses_distance mode ->
        move_view_to_target retarget speed selection.target view

        for other in view.Document.Views.GetViewList(true, false) do
            if not (isNull other) && other.RuntimeSerialNumber <> view.RuntimeSerialNumber then
                move_view_to_target retarget speed selection.target other
    | ValueNone ->
        set_view_target selection.target view

        for other in view.Document.Views.GetViewList(true, false) do
            if not (isNull other) && other.RuntimeSerialNumber <> view.RuntimeSerialNumber then
                set_view_target selection.target other

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

                match ViewTarget.selected_selection_at behavior.retarget mode speed view viewport clientPoint with
                | None -> Ok()
                | Some selection ->
                    apply_selection behavior.retarget mode speed selection view
                    Ok()
    with error ->
        Error $"Could not retarget the viewports: {error.Message}"
