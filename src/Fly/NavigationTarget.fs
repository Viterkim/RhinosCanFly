module RhinosCanFly.NavigationTarget

// Rhino 9 deprecates GetViewList(bool, bool), but Rhino 7 has no replacement.
#nowarn "44"

open Rhino
open Rhino.ApplicationSettings
open Rhino.Display
open Rhino.Geometry

let apply (loaded: ConfigLoadResult) (targetPoint: NavigationTargetPoint) (mode: ViewNavigationMode) (view: RhinoView) =
    try
        if isNull view || isNull view.Document then
            Error "The navigation viewport is unavailable."
        else
            let behavior = loaded.config.behavior

            let retargetMode =
                match mode with
                | ViewNavigationMode.Pivot -> behavior.retarget.on_pivot
                | ViewNavigationMode.Pan -> behavior.retarget.on_pan

            let gumballTarget =
                match mode with
                | ViewNavigationMode.Pivot ->
                    match ViewTarget.gumball_target behavior.use_gumball_as_target view with
                    | Some target when ViewTarget.target_is_in_front view.ActiveViewport target -> Some target
                    | Some _
                    | None -> None
                | ViewNavigationMode.Pan -> None

            match gumballTarget with
            | Some target -> view.ActiveViewport.SetCameraTarget(target, false)
            | None when retargetMode <> RetargetMode.Off ->
                let movement = loaded.config.movement

                let speed =
                    FlightSpeed.current
                        view.Document
                        behavior.load_speed_from_document
                        movement.speed_range
                        movement.base_speed

                ViewTarget.apply_for_navigation
                    behavior.retarget
                    retargetMode
                    mode
                    speed
                    view
                    view.ActiveViewport
                    targetPoint
            | None -> ()

            Ok()
    with error ->
        Error $"Could not set the navigation target: {error.Message}"

let prepare
    (loaded: ConfigLoadResult)
    (host: ViewportHostIdentity)
    (targetPoint: NavigationTargetPoint)
    (mode: ViewNavigationMode)
    =
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
                || document.RuntimeSerialNumber <> host.document_serial_number
            then
                Error "The navigation document is no longer active."
            else
                let activeView = document.Views.ActiveView

                if isNull activeView || activeView.RuntimeSerialNumber <> host.view_serial_number then
                    Error "The navigation viewport is not active yet."
                else
                    let currentHost = PlatformInput.capture_viewport_host view

                    if
                        currentHost.document_serial_number = host.document_serial_number
                        && currentHost.view_serial_number = host.view_serial_number
                        && currentHost.view_window = host.view_window
                        && currentHost.root_window = host.root_window
                    then
                        match apply loaded targetPoint mode view with
                        | Ok() -> Ok currentHost
                        | Error error -> Error error
                    else
                        Error "The navigation viewport changed before it could be prepared."
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

let apply_to_views (scope: RetargetScope) (view: RhinoView) (applyToView: RhinoView -> unit) =
    if not (isNull view) then
        let document = view.Document

        if not (isNull document) then
            if scope = RetargetScope.AllViews then
                applyToView view

            for other in document.Views.GetViewList(true, false) do
                if not (isNull other) && other.RuntimeSerialNumber <> view.RuntimeSerialNumber then
                    applyToView other

let zoom_views_to_selection
    (retarget: RetargetConfig)
    (scope: RetargetScope)
    (target: Point3d)
    (bounds: BoundingBox)
    (view: RhinoView)
    =
    let previousPerspectiveBorder = ViewSettings.ZoomExtentsPerspectiveViewBorder
    let previousParallelBorder = ViewSettings.ZoomExtentsParallelViewBorder

    try
        ViewSettings.ZoomExtentsPerspectiveViewBorder <- retarget.perspective_zoom_border
        ViewSettings.ZoomExtentsParallelViewBorder <- retarget.parallel_zoom_border

        apply_to_views scope view (zoom_view_to_selection target bounds)
    finally
        ViewSettings.ZoomExtentsPerspectiveViewBorder <- previousPerspectiveBorder
        ViewSettings.ZoomExtentsParallelViewBorder <- previousParallelBorder

let set_view_target (target: Point3d) (view: RhinoView) =
    if not (isNull view) then
        view.ActiveViewport.SetCameraTarget(target, false)
        view.Redraw()

let apply_selection
    (retarget: RetargetConfig)
    (scope: RetargetScope)
    (mode: RetargetMode)
    (speed: float)
    (selection: ViewTarget.RetargetSelection)
    (view: RhinoView)
    =
    match selection.bounds with
    | ValueSome bounds -> zoom_views_to_selection retarget scope selection.target bounds view
    | ValueNone when RetargetMode.uses_distance mode ->
        apply_to_views scope view (move_view_to_target retarget speed selection.target)
    | ValueNone -> apply_to_views scope view (set_view_target selection.target)

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
                    Error "The retarget viewport is not active yet."
                else
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
                        apply_selection behavior.retarget RetargetScope.AllViews mode speed selection view
                        Ok()
    with error ->
        Error $"Could not retarget the viewports: {error.Message}"
