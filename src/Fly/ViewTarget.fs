module RhinosCanFly.ViewTarget

open System
open System.Diagnostics
open Rhino
open Rhino.Display
open Rhino.Geometry

let viewport_center (viewport: RhinoViewport) =
    let bounds = viewport.Bounds
    struct (bounds.Width / 2, bounds.Height / 2)

let target_is_in_front (viewport: RhinoViewport) (target: Point3d) =
    let mutable direction = viewport.CameraDirection
    let offset = target - viewport.CameraLocation

    target.IsValid
    && direction.Unitize()
    && Vector3d.Multiply(offset, direction) > RhinoMath.ZeroTolerance

let accepted_candidate
    (source: ViewTargetDebug.CandidateSource)
    (target: Point3d)
    (detail: string)
    : ViewTargetDebug.Candidate =
    { source = source
      target = Some target
      detail = detail }

let rejected_candidate (source: ViewTargetDebug.CandidateSource) (detail: string) : ViewTargetDebug.Candidate =
    { source = source
      target = None
      detail = detail }

let try_geometry_candidate (viewport: RhinoViewport) =
    let struct (x, y) = viewport_center viewport

    use capture = new ZBufferCapture(viewport)
    let hitCount = capture.HitCount()

    if hitCount <= 0 then
        rejected_candidate ViewTargetDebug.Geometry $"Z-buffer reported {hitCount} hits"
    else
        let depth = capture.ZValueAt(x, y)

        if Single.IsNaN depth || Single.IsInfinity depth || depth <= 0.f || depth >= 1.f then
            rejected_candidate ViewTargetDebug.Geometry $"center depth {depth:G9} is outside 0..1"
        else
            let target = capture.WorldPointAt(x, y)

            if target_is_in_front viewport target then
                accepted_candidate
                    ViewTargetDebug.Geometry
                    target
                    $"center ({x}, {y}), depth {depth:G9}, {hitCount} hits"
            else
                rejected_candidate
                    ViewTargetDebug.Geometry
                    $"center point {ViewTargetDebug.point_text target} is not in front of the camera"

let try_geometry_candidate_safely (viewport: RhinoViewport) =
    try
        try_geometry_candidate viewport
    with error ->
        Debug.WriteLine $"RhinosCanFly geometry target: {error}"
        rejected_candidate ViewTargetDebug.Geometry $"Z-buffer failed: {error.Message}"

let distance_candidate (speed: float) (config: ViewTargetConfig) (viewport: RhinoViewport) =
    let (ViewTargetDistanceMultiplier multiplier) = config.distance_multiplier
    let distance = speed * multiplier
    let mutable direction = viewport.CameraDirection

    if
        not (RhinoMath.IsValidDouble distance)
        || distance <= RhinoMath.ZeroTolerance
        || not (direction.Unitize())
    then
        rejected_candidate ViewTargetDebug.Distance $"speed × multiplier produced {distance:G9}"
    else
        let target = viewport.CameraLocation + direction * distance

        if target_is_in_front viewport target then
            accepted_candidate ViewTargetDebug.Distance target $"{speed:G9} × {multiplier:G9} = {distance:G9}"
        else
            rejected_candidate ViewTargetDebug.Distance "target is invalid"

let skipped_candidate (source: ViewTargetDebug.CandidateSource) =
    rejected_candidate source "not used by this mode"

let evaluate (config: ViewTargetConfig) (speed: float) (viewport: RhinoViewport) =
    let distance =
        match config.mode with
        | ViewTargetMode.Distance
        | ViewTargetMode.GeometryThenDistance -> distance_candidate speed config viewport
        | ViewTargetMode.Off
        | _ -> skipped_candidate ViewTargetDebug.Distance

    let geometry =
        match config.mode with
        | ViewTargetMode.GeometryThenDistance -> try_geometry_candidate_safely viewport
        | ViewTargetMode.Off
        | ViewTargetMode.Distance
        | _ -> skipped_candidate ViewTargetDebug.Geometry

    let selected =
        match config.mode with
        | ViewTargetMode.Off -> None
        | ViewTargetMode.Distance ->
            if Option.isSome distance.target then
                Some distance
            else
                None
        | ViewTargetMode.GeometryThenDistance ->
            if Option.isSome geometry.target then Some geometry
            elif Option.isSome distance.target then Some distance
            else None
        | _ -> None

    let evaluation: ViewTargetDebug.Evaluation =
        { geometry = geometry
          distance = distance
          selected = selected }

    evaluation

let selected_target (config: ViewTargetConfig) (speed: float) (viewport: RhinoViewport) =
    match (evaluate config speed viewport).selected with
    | Some candidate -> candidate.target
    | None -> None

let apply_selected (evaluation: ViewTargetDebug.Evaluation) (viewport: RhinoViewport) =
    match evaluation.selected with
    | Some candidate ->
        match candidate.target with
        | Some target -> viewport.SetCameraTarget(target, false)
        | None -> ()
    | None -> ()

let record_debug_snapshot
    (config: ViewTargetConfig)
    (speed: float)
    (viewport: RhinoViewport)
    (applied: bool)
    (beforeTarget: Point3d)
    (evaluation: ViewTargetDebug.Evaluation)
    =

    let snapshot: ViewTargetDebug.Snapshot =
        { viewport_id = viewport.Id
          viewport_name = viewport.Name
          viewport_width = viewport.Bounds.Width
          viewport_height = viewport.Bounds.Height
          config = config
          speed = speed
          camera_location = viewport.CameraLocation
          camera_direction = viewport.CameraDirection
          before_target = beforeTarget
          evaluation = evaluation
          after_target = viewport.CameraTarget
          applied = applied }

    ViewTargetDebug.record snapshot

let apply (config: ViewTargetConfig) (speed: float) (viewport: RhinoViewport) =
    let beforeTarget = viewport.CameraTarget
    let evaluation = evaluate config speed viewport
    apply_selected evaluation viewport

    if ViewTargetDebug.enabled () then
        record_debug_snapshot config speed viewport true beforeTarget evaluation

let debug_current (config: ViewTargetConfig) (speed: float) (viewport: RhinoViewport) =
    let beforeTarget = viewport.CameraTarget
    let evaluation = evaluate config speed viewport
    record_debug_snapshot config speed viewport false beforeTarget evaluation
