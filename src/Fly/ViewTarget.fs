module RhinosCanFly.ViewTarget

open System
open System.Diagnostics
open Rhino
open Rhino.Display
open Rhino.Geometry

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

let selected_target (config: ViewTargetConfig) (speed: float) (viewport: RhinoViewport) =
    match config.mode with
    | ViewTargetMode.Distance -> distance_target config speed viewport
    | ViewTargetMode.GeometryThenDistance ->
        match try_geometry_target viewport with
        | Some target -> Some target
        | None -> distance_target config speed viewport
    | ViewTargetMode.Off
    | _ -> None

let apply (config: ViewTargetConfig) (speed: float) (viewport: RhinoViewport) =
    match selected_target config speed viewport with
    | Some target -> viewport.SetCameraTarget(target, false)
    | None -> ()
