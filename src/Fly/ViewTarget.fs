module RhinosCanFly.ViewTarget

open System
open System.Diagnostics
open Rhino
open Rhino.ApplicationSettings
open Rhino.DocObjects
open Rhino.Display
open Rhino.Geometry
open Rhino.Input.Custom

[<Struct>]
type RetargetSelection =
    { target: Point3d
      bounds: BoundingBox voption }

[<Struct>]
type SelectedObjectPick =
    | BoundsWhenNothingElsePicked
    | ExactUnderCursor

let target_is_in_front (viewport: RhinoViewport) (target: Point3d) =
    let mutable direction = viewport.CameraDirection
    let offset = target - viewport.CameraLocation

    target.IsValid
    && direction.Unitize()
    && Vector3d.Multiply(offset, direction) > RhinoMath.ZeroTolerance

let viewport_center (viewport: RhinoViewport) =
    let bounds = viewport.Bounds

    { x = bounds.Width / 2
      y = bounds.Height / 2 }

let try_geometry_target_at (viewport: RhinoViewport) (point: ViewportClientPoint) =
    try
        use capture = new ZBufferCapture(viewport)

        if capture.HitCount() <= 0 then
            None
        else
            let depth = capture.ZValueAt(point.x, point.y)

            if Single.IsNaN depth || Single.IsInfinity depth || depth <= 0.f || depth >= 1.f then
                None
            else
                let target = capture.WorldPointAt(point.x, point.y)

                if target_is_in_front viewport target then
                    Some target
                else
                    None
    with error ->
        Debug.WriteLine $"RhinosCanFly geometry target: {error}"
        None

let try_geometry_target (viewport: RhinoViewport) =
    try_geometry_target_at viewport (viewport_center viewport)

let object_bounds (accurate: bool) (rhinoObject: RhinoObject) =
    if isNull rhinoObject then
        ValueNone
    else
        let geometry = rhinoObject.Geometry

        if isNull geometry then
            ValueNone
        else
            let bounds = geometry.GetBoundingBox accurate

            if bounds.IsValid then ValueSome bounds else ValueNone

let object_selection (viewport: RhinoViewport) (rhinoObject: RhinoObject) =
    match object_bounds true rhinoObject with
    | ValueSome bounds ->
        let target = bounds.Center

        if target_is_in_front viewport target then
            Some
                { target = target
                  bounds = ValueSome bounds }
        else
            None
    | ValueNone -> None

let selection_filter_allows (enabled: bool) (filter: ObjectType) (rhinoObject: RhinoObject) =
    if not enabled then
        true
    else
        filter = ObjectType.AnyObject
        || (rhinoObject.ObjectType &&& filter) <> ObjectType.None

let try_filtered_selection_at_internal
    (selectedObjectPick: SelectedObjectPick)
    (view: RhinoView)
    (viewport: RhinoViewport)
    (point: ViewportClientPoint)
    =
    try
        if isNull view || isNull view.Document then
            None
        else
            let mutable pickLine = Line.Unset

            if not (viewport.GetFrustumLine(float point.x, float point.y, &pickLine)) then
                None
            else
                let filterEnabled = SelectionFilterSettings.Enabled
                let oneShotFilter = SelectionFilterSettings.OneShotGeometryFilter

                let geometryFilter =
                    if oneShotFilter = ObjectType.None then
                        SelectionFilterSettings.GlobalGeometryFilter
                    else
                        oneShotFilter

                use context = new PickContext()
                context.View <- view
                context.PickLine <- pickLine
                context.PickStyle <- PickStyle.PointPick
                context.PickMode <- PickMode.Shaded
                context.PickGroupsEnabled <- false
                context.SubObjectSelectionEnabled <- false
                context.SetPickTransform(viewport.GetPickTransform(point.x, point.y))
                context.UpdateClippingPlanes()

                let picked = view.Document.Objects.PickObjects context

                let cameraLocation = viewport.CameraLocation
                let mutable cameraDirection = viewport.CameraDirection
                let mutable nearestDepth = Double.PositiveInfinity
                let mutable selectedObject: RhinoObject = null

                try
                    if cameraDirection.Unitize() then
                        if not (isNull picked) then
                            for reference in picked do
                                if not (isNull reference) then
                                    let selectionPoint = reference.SelectionPoint()

                                    if selectionPoint.IsValid then
                                        let depth = Vector3d.Multiply(selectionPoint - cameraLocation, cameraDirection)

                                        if depth > RhinoMath.ZeroTolerance && depth < nearestDepth then
                                            let rhinoObject = reference.Object()

                                            match object_bounds false rhinoObject with
                                            | ValueSome bounds ->
                                                let centerDepth =
                                                    Vector3d.Multiply(bounds.Center - cameraLocation, cameraDirection)

                                                if centerDepth > RhinoMath.ZeroTolerance then
                                                    nearestDepth <- depth
                                                    selectedObject <- rhinoObject
                                            | ValueNone -> ()

                        let exactSelectedHit = selectedObjectPick = ExactUnderCursor
                        let checkSelectedObjects = exactSelectedHit || isNull selectedObject

                        let geometryTarget =
                            if checkSelectedObjects && exactSelectedHit then
                                try_geometry_target_at viewport point
                            else
                                None

                        let geometryDepth =
                            match geometryTarget with
                            | Some target -> Vector3d.Multiply(target - cameraLocation, cameraDirection)
                            | None -> Double.PositiveInfinity

                        if checkSelectedObjects then
                            for rhinoObject in view.Document.Objects.GetSelectedObjects(false, false) do
                                if
                                    not (isNull rhinoObject)
                                    && rhinoObject.Visible
                                    && selection_filter_allows filterEnabled geometryFilter rhinoObject
                                then
                                    match object_bounds false rhinoObject with
                                    | ValueSome bounds ->
                                        let center = bounds.Center
                                        let centerDepth = Vector3d.Multiply(center - cameraLocation, cameraDirection)

                                        if centerDepth > RhinoMath.ZeroTolerance then
                                            let containsGeometry =
                                                match geometryTarget with
                                                | Some target -> bounds.Contains target
                                                | None -> false

                                            let mutable completelyInside = false

                                            let boundsHit =
                                                match geometryTarget with
                                                | Some _ -> containsGeometry
                                                | None -> context.PickFrustumTest(bounds, &completelyInside)

                                            if boundsHit then
                                                let depth =
                                                    if containsGeometry && geometryDepth > RhinoMath.ZeroTolerance then
                                                        geometryDepth
                                                    else
                                                        centerDepth

                                                if depth > RhinoMath.ZeroTolerance && depth < nearestDepth then
                                                    nearestDepth <- depth
                                                    selectedObject <- rhinoObject
                                    | ValueNone -> ()

                    if isNull selectedObject then
                        None
                    else
                        // Candidate rejection uses Rhino's cached estimate. Compute the tight box once,
                        // after the nearest object is known.
                        object_selection viewport selectedObject
                finally
                    if not (isNull picked) then
                        for reference in picked do
                            if not (isNull reference) then
                                reference.Dispose()
    with error ->
        Debug.WriteLine $"RhinosCanFly filtered target: {error}"
        None

let try_filtered_selection_at (view: RhinoView) (viewport: RhinoViewport) (point: ViewportClientPoint) =
    try_filtered_selection_at_internal ExactUnderCursor view viewport point

let try_filtered_target_at (view: RhinoView) (viewport: RhinoViewport) (point: ViewportClientPoint) =
    match try_filtered_selection_at_internal BoundsWhenNothingElsePicked view viewport point with
    | Some selection -> Some selection.target
    | None -> None

let try_filtered_target (view: RhinoView) (viewport: RhinoViewport) =
    try_filtered_target_at view viewport (viewport_center viewport)

let try_object_center_at
    (select: RhinoView -> RhinoViewport -> ViewportClientPoint -> 'Target option)
    (view: RhinoView)
    (viewport: RhinoViewport)
    (point: ViewportClientPoint)
    =
    try
        let selectionFilter = SelectionFilterSettings.GetCurrentState()

        try
            SelectionFilterSettings.GlobalGeometryFilter <- ObjectType.AnyObject
            SelectionFilterSettings.OneShotGeometryFilter <- ObjectType.AnyObject
            SelectionFilterSettings.Enabled <- false
            SelectionFilterSettings.SubObjectSelect <- false
            select view viewport point
        finally
            SelectionFilterSettings.UpdateFromState selectionFilter
    with error ->
        Debug.WriteLine $"RhinosCanFly object center selection: {error}"
        None

let try_object_center_selection_at (view: RhinoView) (viewport: RhinoViewport) (point: ViewportClientPoint) =
    try_object_center_at try_filtered_selection_at view viewport point

let try_object_center_target_at (view: RhinoView) (viewport: RhinoViewport) (point: ViewportClientPoint) =
    try_object_center_at try_filtered_target_at view viewport point

let try_object_center_target (view: RhinoView) (viewport: RhinoViewport) =
    try_object_center_target_at view viewport (viewport_center viewport)

let retarget_distance (config: RetargetConfig) (speed: float) (viewport: RhinoViewport) =
    let (RetargetFallbackMultiplier multiplier) =
        if viewport.IsParallelProjection then
            config.parallel_fallback_multiplier
        else
            config.perspective_fallback_multiplier

    let distance = speed * multiplier

    if RhinoMath.IsValidDouble distance && distance > RhinoMath.ZeroTolerance then
        Some distance
    else
        None

let parallel_magnification_to_distance (viewport: RhinoViewport) (target: Point3d) (distance: float) =
    if not viewport.IsParallelProjection then
        1.
    else
        let mutable direction = viewport.CameraDirection

        if direction.Unitize() then
            let depth = Vector3d.Multiply(target - viewport.CameraLocation, direction)
            let factor = depth / distance

            if RhinoMath.IsValidDouble factor && factor > RhinoMath.ZeroTolerance then
                factor
            else
                1.
        else
            1.

let distance_target (config: RetargetConfig) (speed: float) (viewport: RhinoViewport) =
    let mutable direction = viewport.CameraDirection

    match retarget_distance config speed viewport with
    | Some distance when direction.Unitize() ->
        let target = viewport.CameraLocation + direction * distance

        if target_is_in_front viewport target then
            Some target
        else
            None
    | Some _
    | None -> None

let distance_target_at (config: RetargetConfig) (speed: float) (viewport: RhinoViewport) (point: ViewportClientPoint) =
    let mutable ray = Line.Unset

    match retarget_distance config speed viewport with
    | Some distance when viewport.GetFrustumLine(float point.x, float point.y, &ray) ->
        let mutable direction = ray.Direction

        if direction.Unitize() then
            let camera = viewport.CameraLocation
            let rayDepth = Vector3d.Multiply(ray.From - camera, direction)

            Some(ray.From + direction * (distance - rayDepth))
        else
            None
    | Some _
    | None -> None

let distance_selection_at
    (config: RetargetConfig)
    (speed: float)
    (viewport: RhinoViewport)
    (point: ViewportClientPoint)
    =
    match distance_target_at config speed viewport point with
    | Some target -> Some { target = target; bounds = ValueNone }
    | None -> None

let try_geometry_selection_at (view: RhinoView) (viewport: RhinoViewport) (point: ViewportClientPoint) =
    match try_geometry_target_at viewport point with
    | None -> None
    | Some target ->
        let bounds =
            match try_object_center_selection_at view viewport point with
            | Some selection -> selection.bounds
            | None -> ValueNone

        Some { target = target; bounds = bounds }

let selected_target
    (config: RetargetConfig)
    (mode: RetargetMode)
    (speed: float)
    (view: RhinoView)
    (viewport: RhinoViewport)
    =
    match mode with
    | RetargetMode.Distance -> distance_target config speed viewport
    | RetargetMode.Geometry -> try_geometry_target viewport
    | RetargetMode.GeometryThenDistance ->
        match try_geometry_target viewport with
        | Some target -> Some target
        | None -> distance_target config speed viewport
    | RetargetMode.Target -> try_filtered_target view viewport
    | RetargetMode.TargetThenDistance ->
        match try_filtered_target view viewport with
        | Some target -> Some target
        | None -> distance_target config speed viewport
    | RetargetMode.ObjectCenter -> try_object_center_target view viewport
    | RetargetMode.ObjectCenterThenDistance ->
        match try_object_center_target view viewport with
        | Some target -> Some target
        | None -> distance_target config speed viewport
    | RetargetMode.Off
    | _ -> None

let apply (config: RetargetConfig) (mode: RetargetMode) (speed: float) (view: RhinoView) (viewport: RhinoViewport) =
    match selected_target config mode speed view viewport with
    | Some target -> viewport.SetCameraTarget(target, false)
    | None -> ()

let apply_for_navigation
    (config: RetargetConfig)
    (retargetMode: RetargetMode)
    (navigationMode: ViewNavigationMode)
    (speed: float)
    (view: RhinoView)
    (viewport: RhinoViewport)
    =
    match selected_target config retargetMode speed view viewport with
    | Some selectedTarget ->
        let target =
            match navigationMode with
            | ViewNavigationMode.Pivot -> selectedTarget
            | ViewNavigationMode.Pan ->
                // Pan only needs the picked depth. An off-axis camera target would turn the view
                // before Rhino applies the first lateral dolly, making that first frame jump.
                Movement.target_on_camera_axis viewport.CameraLocation selectedTarget viewport.CameraDirection

        viewport.SetCameraTarget(target, false)
    | None -> ()

let selected_selection_at
    (config: RetargetConfig)
    (mode: RetargetMode)
    (speed: float)
    (view: RhinoView)
    (viewport: RhinoViewport)
    (point: ViewportClientPoint)
    =
    match mode with
    | RetargetMode.Distance -> distance_selection_at config speed viewport point
    | RetargetMode.Geometry -> try_geometry_selection_at view viewport point
    | RetargetMode.GeometryThenDistance ->
        match try_geometry_selection_at view viewport point with
        | Some selection -> Some selection
        | None -> distance_selection_at config speed viewport point
    | RetargetMode.Target -> try_filtered_selection_at view viewport point
    | RetargetMode.TargetThenDistance ->
        match try_filtered_selection_at view viewport point with
        | Some selection -> Some selection
        | None -> distance_selection_at config speed viewport point
    | RetargetMode.ObjectCenter -> try_object_center_selection_at view viewport point
    | RetargetMode.ObjectCenterThenDistance ->
        match try_object_center_selection_at view viewport point with
        | Some selection -> Some selection
        | None -> distance_selection_at config speed viewport point
    | RetargetMode.Off
    | _ -> None
