module RhinosCanFly.ViewTarget

open System
open System.Diagnostics
open Rhino
open Rhino.ApplicationSettings
open Rhino.DocObjects
open Rhino.Display
open Rhino.Geometry
open Rhino.Input.Custom
open Rhino.UI.Gumball

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

let object_gumball_target (viewport: RhinoViewport) (rhinoObject: RhinoObject) =
    if isNull rhinoObject then
        None
    else
        let mutable bounds = BoundingBox.Unset

        if
            not (RhinoObject.GetTightBoundingBox([| rhinoObject |], &bounds))
            || not bounds.IsValid
        then
            None
        else
            use gumball = new GumballObject()

            if gumball.SetFromBoundingBox bounds then
                let target = gumball.Frame.Plane.Origin

                if target_is_in_front viewport target then
                    Some target
                else
                    None
            else
                None

let try_filtered_target_at (view: RhinoView) (viewport: RhinoViewport) (point: ViewportClientPoint) =
    try
        if isNull view || isNull view.Document then
            None
        else
            let mutable pickLine = Line.Unset

            if not (viewport.GetFrustumLine(float point.x, float point.y, &pickLine)) then
                None
            else
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

                if isNull picked || picked.Length = 0 then
                    None
                else
                    let cameraLocation = viewport.CameraLocation
                    let mutable cameraDirection = viewport.CameraDirection
                    let mutable nearestDepth = Double.PositiveInfinity
                    let mutable target = None

                    try
                        if cameraDirection.Unitize() then
                            for reference in picked do
                                if not (isNull reference) then
                                    let selectionPoint = reference.SelectionPoint()

                                    if selectionPoint.IsValid then
                                        let depth = Vector3d.Multiply(selectionPoint - cameraLocation, cameraDirection)

                                        if depth > RhinoMath.ZeroTolerance && depth < nearestDepth then
                                            match object_gumball_target viewport (reference.Object()) with
                                            | Some objectTarget ->
                                                nearestDepth <- depth
                                                target <- Some objectTarget
                                            | None -> ()

                        target
                    finally
                        for reference in picked do
                            if not (isNull reference) then
                                reference.Dispose()
    with error ->
        Debug.WriteLine $"RhinosCanFly filtered target: {error}"
        None

let try_filtered_target (view: RhinoView) (viewport: RhinoViewport) =
    try_filtered_target_at view viewport (viewport_center viewport)

let try_object_center_target_at (view: RhinoView) (viewport: RhinoViewport) (point: ViewportClientPoint) =
    try
        let selectionFilter = SelectionFilterSettings.GetCurrentState()

        try
            SelectionFilterSettings.GlobalGeometryFilter <- ObjectType.AnyObject
            SelectionFilterSettings.OneShotGeometryFilter <- ObjectType.AnyObject
            SelectionFilterSettings.Enabled <- false
            SelectionFilterSettings.SubObjectSelect <- false
            try_filtered_target_at view viewport point
        finally
            SelectionFilterSettings.UpdateFromState selectionFilter
    with error ->
        Debug.WriteLine $"RhinosCanFly object center target: {error}"
        None

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

let selected_target
    (config: RetargetConfig)
    (mode: RetargetMode)
    (speed: float)
    (view: RhinoView)
    (viewport: RhinoViewport)
    =
    match mode with
    | RetargetMode.Distance -> distance_target config speed viewport
    | RetargetMode.GeometryThenDistance ->
        match try_geometry_target viewport with
        | Some target -> Some target
        | None -> distance_target config speed viewport
    | RetargetMode.TargetThenDistance ->
        match try_filtered_target view viewport with
        | Some target -> Some target
        | None -> distance_target config speed viewport
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

let selected_target_at
    (config: RetargetConfig)
    (mode: RetargetMode)
    (speed: float)
    (view: RhinoView)
    (viewport: RhinoViewport)
    (point: ViewportClientPoint)
    =
    match mode with
    | RetargetMode.Distance -> distance_target_at config speed viewport point
    | RetargetMode.GeometryThenDistance ->
        match try_geometry_target_at viewport point with
        | Some target -> Some target
        | None -> distance_target_at config speed viewport point
    | RetargetMode.TargetThenDistance ->
        match try_filtered_target_at view viewport point with
        | Some target -> Some target
        | None -> distance_target_at config speed viewport point
    | RetargetMode.ObjectCenterThenDistance ->
        match try_object_center_target_at view viewport point with
        | Some target -> Some target
        | None -> distance_target_at config speed viewport point
    | RetargetMode.Off
    | _ -> None
