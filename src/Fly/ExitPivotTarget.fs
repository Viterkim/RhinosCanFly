module RhinosCanFly.ExitPivotTarget

open System
open System.Diagnostics
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

let accepted_candidate
    (source: ExitPivotTargetDebug.CandidateSource)
    (target: Point3d)
    (detail: string)
    : ExitPivotTargetDebug.Candidate =
    { source = source
      target = Some target
      detail = detail }

let rejected_candidate
    (source: ExitPivotTargetDebug.CandidateSource)
    (detail: string)
    : ExitPivotTargetDebug.Candidate =
    { source = source
      target = None
      detail = detail }

let try_geometry_candidate (viewport: RhinoViewport) =
    let struct (x, y) = viewport_center viewport

    use capture = new ZBufferCapture(viewport)
    let hitCount = capture.HitCount()

    if hitCount <= 0 then
        rejected_candidate ExitPivotTargetDebug.Geometry $"Z-buffer reported {hitCount} hits"
    else
        let depth = capture.ZValueAt(x, y)

        if Single.IsNaN depth || Single.IsInfinity depth || depth <= 0.f || depth >= 1.f then
            rejected_candidate ExitPivotTargetDebug.Geometry $"center depth {depth:G9} is outside 0..1"
        else
            let target = capture.WorldPointAt(x, y)

            if valid_target viewport target then
                accepted_candidate
                    ExitPivotTargetDebug.Geometry
                    target
                    $"center ({x}, {y}), depth {depth:G9}, {hitCount} hits"
            else
                rejected_candidate
                    ExitPivotTargetDebug.Geometry
                    $"center point {ExitPivotTargetDebug.point_text target} failed front/frustum validation"

let try_geometry_candidate_safely (viewport: RhinoViewport) =
    try
        try_geometry_candidate viewport
    with error ->
        Debug.WriteLine $"RhinosCanFly exit geometry target: {error}"
        rejected_candidate ExitPivotTargetDebug.Geometry $"Z-buffer failed: {error.Message}"

let try_plane_candidate (source: ExitPivotTargetDebug.CandidateSource) (viewport: RhinoViewport) (plane: Plane) =
    if not plane.IsValid then
        rejected_candidate source "plane is invalid"
    else
        let struct (x, y) = viewport_center viewport
        let line = viewport.ClientToWorld(Point2d(float x, float y))
        let mutable lineParameter = 0.
        let mutable lineDirection = line.Direction

        let rayTarget =
            if
                line.IsValid
                && lineDirection.Unitize()
                && abs (Vector3d.Multiply(lineDirection, plane.Normal)) > 1e-6
                && Intersection.LinePlane(line, plane, &lineParameter)
                && RhinoMath.IsValidDouble lineParameter
            then
                let target = line.PointAt lineParameter

                if valid_target viewport target then
                    Some(accepted_candidate source target $"center-ray intersection at t={lineParameter:G9}")
                else
                    None
            else
                None

        match rayTarget with
        | Some candidate -> candidate
        | None ->
            let target = plane.ClosestPoint viewport.CameraTarget

            if valid_target viewport target then
                accepted_candidate source target "camera-target projection fallback"
            else
                rejected_candidate
                    source
                    $"center ray and projected target {ExitPivotTargetDebug.point_text target} failed validation"

let candidate_target (candidate: ExitPivotTargetDebug.Candidate) = candidate.target

let try_geometry_target_safely (viewport: RhinoViewport) =
    try_geometry_candidate_safely viewport |> candidate_target

let try_plane_target (source: ExitPivotTargetDebug.CandidateSource) (viewport: RhinoViewport) (plane: Plane) =
    try_plane_candidate source viewport plane |> candidate_target

let try_target (mode: ExitPivotTargetMode) (viewport: RhinoViewport) =
    match mode with
    | ExitPivotTargetMode.Off -> None
    | ExitPivotTargetMode.Geometry -> try_geometry_target_safely viewport
    | ExitPivotTargetMode.GeometryThenCPlane ->
        match try_geometry_target_safely viewport with
        | Some target -> Some target
        | None -> try_plane_target ExitPivotTargetDebug.ConstructionPlane viewport (viewport.ConstructionPlane())
    | ExitPivotTargetMode.GeometryThenWorldXY ->
        match try_geometry_target_safely viewport with
        | Some target -> Some target
        | None -> try_plane_target ExitPivotTargetDebug.WorldXY viewport Plane.WorldXY
    | _ -> None

let evaluate_all (viewport: RhinoViewport) =
    struct (try_geometry_candidate_safely viewport,
            try_plane_candidate ExitPivotTargetDebug.ConstructionPlane viewport (viewport.ConstructionPlane()),
            try_plane_candidate ExitPivotTargetDebug.WorldXY viewport Plane.WorldXY)

let selected_candidate
    (mode: ExitPivotTargetMode)
    (geometry: ExitPivotTargetDebug.Candidate)
    (constructionPlane: ExitPivotTargetDebug.Candidate)
    (worldXY: ExitPivotTargetDebug.Candidate)
    =
    match mode with
    | ExitPivotTargetMode.Off -> None
    | ExitPivotTargetMode.Geometry ->
        if Option.isSome geometry.target then
            Some geometry
        else
            None
    | ExitPivotTargetMode.GeometryThenCPlane ->
        if Option.isSome geometry.target then
            Some geometry
        elif Option.isSome constructionPlane.target then
            Some constructionPlane
        else
            None
    | ExitPivotTargetMode.GeometryThenWorldXY ->
        if Option.isSome geometry.target then Some geometry
        elif Option.isSome worldXY.target then Some worldXY
        else None
    | _ -> None

let record_debug_snapshot (mode: ExitPivotTargetMode) (viewport: RhinoViewport) (applied: bool) =
    let beforeTarget = viewport.CameraTarget
    let struct (geometry, constructionPlane, worldXY) = evaluate_all viewport
    let selected = selected_candidate mode geometry constructionPlane worldXY

    if applied then
        match selected with
        | Some candidate ->
            match candidate.target with
            | Some target -> viewport.SetCameraTarget(target, false)
            | None -> ()
        | None -> ()

    let snapshot: ExitPivotTargetDebug.Snapshot =
        { viewport_id = viewport.Id
          viewport_name = viewport.Name
          viewport_width = viewport.Bounds.Width
          viewport_height = viewport.Bounds.Height
          mode = mode
          camera_location = viewport.CameraLocation
          camera_direction = viewport.CameraDirection
          before_target = beforeTarget
          geometry = geometry
          construction_plane = constructionPlane
          world_xy = worldXY
          selected = selected
          after_target = viewport.CameraTarget
          applied = applied }

    ExitPivotTargetDebug.record snapshot

let apply (mode: ExitPivotTargetMode) (viewport: RhinoViewport) =
    if ExitPivotTargetDebug.enabled () then
        record_debug_snapshot mode viewport true
    else
        match try_target mode viewport with
        | Some target -> viewport.SetCameraTarget(target, false)
        | None -> ()

let debug_current (mode: ExitPivotTargetMode) (viewport: RhinoViewport) =
    record_debug_snapshot mode viewport false
