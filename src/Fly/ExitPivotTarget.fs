module RhinosCanFly.ExitPivotTarget

open System
open Rhino
open Rhino.Display
open Rhino.Geometry
open Rhino.Geometry.Intersect

let viewport_center (viewport: RhinoViewport) =
    let bounds = viewport.Bounds
    struct (bounds.Width / 2, bounds.Height / 2)

let target_is_in_front (viewport: RhinoViewport) (target: Point3d) =
    let mutable direction = viewport.CameraDirection
    let offset = target - viewport.CameraLocation

    target.IsValid
    && direction.Unitize()
    && Vector3d.Multiply(offset, direction) > RhinoMath.ZeroTolerance

let target_distance_is_reasonable (viewport: RhinoViewport) (target: Point3d) =
    let mutable left = 0.
    let mutable right = 0.
    let mutable bottom = 0.
    let mutable top = 0.
    let mutable nearDistance = 0.
    let mutable farDistance = 0.
    let distance = viewport.CameraLocation.DistanceTo target

    RhinoMath.IsValidDouble distance
    && viewport.GetFrustum(&left, &right, &bottom, &top, &nearDistance, &farDistance)
    && RhinoMath.IsValidDouble nearDistance
    && RhinoMath.IsValidDouble farDistance
    && nearDistance > 0.
    && farDistance > nearDistance
    && distance >= nearDistance * 0.5
    && distance <= farDistance * 1.01

let valid_target (viewport: RhinoViewport) (target: Point3d) =
    target_is_in_front viewport target
    && target_distance_is_reasonable viewport target

let try_geometry_target (viewport: RhinoViewport) =
    let struct (x, y) = viewport_center viewport

    use capture = new ZBufferCapture(viewport)

    if capture.HitCount() <= 0 then
        None
    else
        let depth = capture.ZValueAt(x, y)

        if Single.IsNaN depth || Single.IsInfinity depth || depth <= 0.f || depth >= 1.f then
            None
        else
            let target = capture.WorldPointAt(x, y)

            if valid_target viewport target then Some target else None

let try_plane_target (viewport: RhinoViewport) (plane: Plane) =
    let struct (x, y) = viewport_center viewport
    let line = viewport.ClientToWorld(Point2d(float x, float y))
    let mutable lineParameter = 0.
    let mutable lineDirection = line.Direction

    let sufficientlyTransverse =
        lineDirection.Unitize()
        && abs (Vector3d.Multiply(lineDirection, plane.Normal)) > 1e-6

    if
        plane.IsValid
        && line.IsValid
        && sufficientlyTransverse
        && Intersection.LinePlane(line, plane, &lineParameter)
        && RhinoMath.IsValidDouble lineParameter
    then
        let target = line.PointAt lineParameter

        if valid_target viewport target then Some target else None
    else
        None

let try_target (mode: ExitPivotTargetMode) (viewport: RhinoViewport) =
    match mode with
    | ExitPivotTargetMode.Off -> None
    | ExitPivotTargetMode.Geometry -> try_geometry_target viewport
    | ExitPivotTargetMode.GeometryThenCPlane ->
        match try_geometry_target viewport with
        | Some target -> Some target
        | None -> try_plane_target viewport (viewport.ConstructionPlane())
    | ExitPivotTargetMode.GeometryThenWorldXY ->
        match try_geometry_target viewport with
        | Some target -> Some target
        | None -> try_plane_target viewport Plane.WorldXY
    | _ -> None

let apply (mode: ExitPivotTargetMode) (viewport: RhinoViewport) =
    match try_target mode viewport with
    | Some target -> viewport.SetCameraTarget(target, false)
    | None -> ()
