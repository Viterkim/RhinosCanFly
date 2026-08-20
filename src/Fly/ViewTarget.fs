module RhinosCanFly.ViewTarget

open System
open System.Diagnostics
open Rhino
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

let try_geometry_target (viewport: RhinoViewport) =
    try
        let bounds = viewport.Bounds
        let x = bounds.Width / 2
        let y = bounds.Height / 2

        use capture = new ZBufferCapture(viewport)

        if capture.HitCount() <= 0 then
            None
        else
            let depth = capture.ZValueAt(x, y)

            if Single.IsNaN depth || Single.IsInfinity depth || depth <= 0.f || depth >= 1.f then
                None
            else
                let target = capture.WorldPointAt(x, y)

                if target_is_in_front viewport target then
                    Some target
                else
                    None
    with error ->
        Debug.WriteLine $"RhinosCanFly geometry target: {error}"
        None

let object_gumball_target (viewport: RhinoViewport) (rhinoObject: RhinoObject) =
    if isNull rhinoObject then
        None
    else
        let geometry = rhinoObject.Geometry

        let bounds =
            if isNull geometry then
                BoundingBox.Unset
            else
                geometry.GetBoundingBox(true)

        if not bounds.IsValid then
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

let try_object_center_target (view: RhinoView) (viewport: RhinoViewport) =
    try
        if isNull view || isNull view.Document then
            None
        else
            let bounds = viewport.Bounds
            let x = bounds.Left + bounds.Width / 2
            let y = bounds.Top + bounds.Height / 2
            let mutable pickLine = Line.Unset

            if not (viewport.GetFrustumLine(float x, float y, &pickLine)) then
                None
            else
                use context = new PickContext()
                context.View <- view
                context.PickLine <- pickLine
                context.PickStyle <- PickStyle.PointPick
                context.PickMode <- PickMode.Shaded
                context.PickGroupsEnabled <- false
                context.SubObjectSelectionEnabled <- false
                context.SetPickTransform(viewport.GetPickTransform(x, y))
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
        Debug.WriteLine $"RhinosCanFly object center target: {error}"
        None

let distance_target (config: ViewTargetConfig) (speed: float) (viewport: RhinoViewport) =
    let (ViewTargetDistanceMultiplier multiplier) = config.distance_multiplier
    let distance = speed * multiplier
    let mutable direction = viewport.CameraDirection

    if
        not (RhinoMath.IsValidDouble distance)
        || distance <= RhinoMath.ZeroTolerance
        || not (direction.Unitize())
    then
        None
    else
        let target = viewport.CameraLocation + direction * distance

        if target_is_in_front viewport target then
            Some target
        else
            None

let selected_target (config: ViewTargetConfig) (speed: float) (view: RhinoView) (viewport: RhinoViewport) =
    match config.mode with
    | ViewTargetMode.Distance -> distance_target config speed viewport
    | ViewTargetMode.GeometryThenDistance ->
        match try_geometry_target viewport with
        | Some target -> Some target
        | None -> distance_target config speed viewport
    | ViewTargetMode.ObjectCenterThenDistance ->
        match try_object_center_target view viewport with
        | Some target -> Some target
        | None -> distance_target config speed viewport
    | ViewTargetMode.Off
    | _ -> None

let apply (config: ViewTargetConfig) (speed: float) (view: RhinoView) (viewport: RhinoViewport) =
    match selected_target config speed view viewport with
    | Some target -> viewport.SetCameraTarget(target, false)
    | None -> ()
