module RhinosCanFly.NavigationTarget

// Rhino 9 deprecates GetViewList(bool, bool), but Rhino 7 has no replacement.
#nowarn "44"

open Rhino
open Rhino.ApplicationSettings
open Rhino.Display
open Rhino.Geometry

let apply
    (loaded: ConfigLoadResult)
    (target_point: NavigationTargetPoint)
    (mode: ViewNavigationMode)
    (view: RhinoView)
    =
    try
        if isNull view || isNull view.Document then
            Error "The navigation viewport is unavailable."
        else
            let behavior = loaded.config.behavior

            let retarget_mode =
                match mode with
                | ViewNavigationMode.Pivot -> behavior.retarget.on_pivot
                | ViewNavigationMode.Pan -> behavior.retarget.on_pan

            let prioritized_target =
                match mode with
                | ViewNavigationMode.Pivot ->
                    ViewTarget.prioritized_target behavior.prioritized_target view view.ActiveViewport
                | ViewNavigationMode.Pan -> None

            match prioritized_target with
            | Some target -> view.ActiveViewport.SetCameraTarget(target, false)
            | None when retarget_mode <> RetargetMode.Off ->
                let movement = loaded.config.movement

                let speed =
                    FlightSpeed.current
                        view.Document
                        behavior.load_speed_from_document
                        movement.speed_range
                        movement.base_speed

                ViewTarget.apply_for_navigation
                    behavior.retarget
                    retarget_mode
                    mode
                    speed
                    view
                    view.ActiveViewport
                    target_point
            | None -> ()

            Ok()
    with error ->
        Error $"Could not set the navigation target: {error.Message}"

let prepare
    (loaded: ConfigLoadResult)
    (host: ViewportHostIdentity)
    (target_point: NavigationTargetPoint)
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
            let active_document = RhinoDoc.ActiveDoc

            if
                isNull active_document
                || active_document.RuntimeSerialNumber <> host.document_serial_number
                || document.RuntimeSerialNumber <> host.document_serial_number
            then
                Error "The navigation document is no longer active."
            else
                let active_view = document.Views.ActiveView

                if isNull active_view || active_view.RuntimeSerialNumber <> host.view_serial_number then
                    Error "The navigation viewport is not active yet."
                else
                    let current_host = PlatformInput.capture_viewport_host view

                    if
                        current_host.document_serial_number = host.document_serial_number
                        && current_host.view_serial_number = host.view_serial_number
                        && current_host.view_window = host.view_window
                        && current_host.root_window = host.root_window
                    then
                        match apply loaded target_point mode view with
                        | Ok() -> Ok current_host
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

let apply_to_views (scope: RetargetScope) (view: RhinoView) (apply_to_view: RhinoView -> unit) =
    if not (isNull view) then
        let document = view.Document

        if not (isNull document) then
            if scope = RetargetScope.AllViews then
                apply_to_view view

            for other in document.Views.GetViewList(true, false) do
                if not (isNull other) && other.RuntimeSerialNumber <> view.RuntimeSerialNumber then
                    apply_to_view other

let zoom_views_to_selection
    (retarget: RetargetConfig)
    (scope: RetargetScope)
    (target: Point3d)
    (bounds: BoundingBox)
    (view: RhinoView)
    =
    let previous_perspective_border = ViewSettings.ZoomExtentsPerspectiveViewBorder
    let previous_parallel_border = ViewSettings.ZoomExtentsParallelViewBorder

    try
        ViewSettings.ZoomExtentsPerspectiveViewBorder <- retarget.perspective_zoom_border
        ViewSettings.ZoomExtentsParallelViewBorder <- retarget.parallel_zoom_border

        apply_to_views scope view (zoom_view_to_selection target bounds)
    finally
        ViewSettings.ZoomExtentsPerspectiveViewBorder <- previous_perspective_border
        ViewSettings.ZoomExtentsParallelViewBorder <- previous_parallel_border

let set_view_target (target: Point3d) (view: RhinoView) =
    if not (isNull view) then
        view.ActiveViewport.SetCameraTarget(target, false)
        view.Redraw()

let apply_selection
    (retarget: RetargetConfig)
    (scope: RetargetScope)
    (speed: float)
    (selection: ViewTarget.RetargetSelection)
    (view: RhinoView)
    =
    match selection.bounds with
    | ValueSome bounds -> zoom_views_to_selection retarget scope selection.target bounds view
    | ValueNone when selection.used_distance_fallback ->
        apply_to_views scope view (move_view_to_target retarget speed selection.target)
    | ValueNone -> apply_to_views scope view (set_view_target selection.target)

let retarget
    (loaded: ConfigLoadResult)
    (host: ViewportHostIdentity)
    (client_point: ViewportClientPoint)
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
            let active_document = RhinoDoc.ActiveDoc

            if
                isNull active_document
                || active_document.RuntimeSerialNumber <> host.document_serial_number
            then
                Error "The retarget document is no longer active."
            else
                let active_view = document.Views.ActiveView

                if
                    isNull active_view
                    || active_view.RuntimeSerialNumber <> view.RuntimeSerialNumber
                then
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

                    match ViewTarget.selected_selection_at behavior.retarget mode speed view viewport client_point with
                    | None -> Ok()
                    | Some selection ->
                        apply_selection behavior.retarget RetargetScope.AllViews speed selection view
                        Ok()
    with error ->
        Error $"Could not retarget the viewports: {error.Message}"
