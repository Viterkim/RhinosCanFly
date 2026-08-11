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

let try_geometry_target (viewport: RhinoViewport) =
    let struct (x, y) = viewport_center viewport

    use capture = new ZBufferCapture(viewport)

    if capture.HitCount() = 0 then
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

let try_plane_target (viewport: RhinoViewport) (plane: Plane) =
    let struct (x, y) = viewport_center viewport
    let line = viewport.ClientToWorld(Point2d(float x, float y))
    let mutable lineParameter = 0.

    if
        plane.IsValid
        && line.IsValid
        && Intersection.LinePlane(line, plane, &lineParameter)
        && RhinoMath.IsValidDouble lineParameter
    then
        let target = line.PointAt lineParameter

        if target_is_in_front viewport target then
            Some target
        else
            None
    else
        None

let try_target (mode: AutoPivotTargetMode) (viewport: RhinoViewport) =
    match mode with
    | AutoPivotTargetMode.Off -> None
    | AutoPivotTargetMode.Geometry -> try_geometry_target viewport
    | AutoPivotTargetMode.GeometryThenCPlane ->
        match try_geometry_target viewport with
        | Some target -> Some target
        | None -> try_plane_target viewport (viewport.ConstructionPlane())
    | AutoPivotTargetMode.GeometryThenWorldXY ->
        match try_geometry_target viewport with
        | Some target -> Some target
        | None -> try_plane_target viewport Plane.WorldXY
    | _ -> None

let apply (mode: AutoPivotTargetMode) (viewport: RhinoViewport) =
    match try_target mode viewport with
    | Some target -> viewport.SetCameraTarget(target, false)
    | None -> ()
