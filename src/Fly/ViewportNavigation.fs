module RhinosCanFly.ViewportNavigation

open System
open Rhino
open Rhino.ApplicationSettings
open Rhino.Display
open Rhino.Geometry

[<RequireQualifiedAccess; Struct>]
type Operation =
    | Pivot
    | Pan
    | ParallelPan
    | ParallelZoom

[<Struct>]
type MouseConfig =
    { x_mode: MouseAxisMode
      y_mode: MouseAxisMode
      sensitivity: RuntimeMouseSensitivity
      pivot_multiplier: MousePivotMultiplier
      pan_multiplier: MousePanMultiplier }

let mouse_config (config: ViewNavigationMouseConfig) (isParallel: bool) =
    if isParallel then
        { x_mode = config.x_mode
          y_mode = config.y_mode
          sensitivity = config.parallel_sensitivity
          pivot_multiplier = config.parallel_pivot_multiplier
          pan_multiplier = config.parallel_pan_multiplier }
    else
        { x_mode = config.x_mode
          y_mode = config.y_mode
          sensitivity = config.perspective_sensitivity
          pivot_multiplier = config.perspective_pivot_multiplier
          pan_multiplier = config.perspective_pan_multiplier }

let apply_pivot (viewport: RhinoViewport) (config: MouseConfig) (center: Point3d) (dx: int64) (dy: int64) =
    let (MousePivotMultiplier multiplier) = config.pivot_multiplier

    let deltas =
        Movement.mouse_angle_deltas config.x_mode config.y_mode config.sensitivity multiplier dx dy

    let mutable changed = false

    if
        deltas.yaw_delta <> 0.
        && viewport.Rotate(deltas.yaw_delta, Vector3d.ZAxis, center)
    then
        changed <- true

    if deltas.pitch_delta <> 0. then
        let mutable right = viewport.CameraX

        if right.Unitize() && viewport.Rotate(deltas.pitch_delta, right, center) then
            changed <- true

    changed

let apply_pan (viewport: RhinoViewport) (config: MouseConfig) (dx: int64) (dy: int64) =
    let (MousePanMultiplier multiplier) = config.pan_multiplier

    let deltas =
        Movement.mouse_angle_deltas config.x_mode config.y_mode config.sensitivity multiplier dx dy

    let location = viewport.CameraLocation
    let target = viewport.CameraTarget
    let mutable direction = viewport.CameraDirection
    let mutable right = viewport.CameraX
    let mutable up = viewport.CameraY

    if direction.Unitize() && right.Unitize() && up.Unitize() then
        let requestedDepth = Vector3d.Multiply(target - location, direction)

        let depth =
            if
                RhinoMath.IsValidDouble requestedDepth
                && requestedDepth > RhinoMath.ZeroTolerance
            then
                requestedDepth
            else
                1.

        let translation = right * deltas.yaw_delta * depth - up * deltas.pitch_delta * depth

        if translation.IsZero then
            false
        else
            viewport.SetCameraLocation(location + translation, false)
            viewport.SetCameraTarget(target + translation, false)
            true
    else
        false

let parallel_zoom_exponent (dy: int64) =
    let zoomScale = ViewSettings.ZoomScale

    if
        dy <> 0L
        && not (Double.IsNaN zoomScale)
        && not (Double.IsInfinity zoomScale)
        && zoomScale > 0.
        && zoomScale <> 1.
    then
        let steps = float -dy / 12.
        steps * Math.Log(1. / zoomScale)
    else
        0.

let wheel_magnification (steps: int64) =
    let zoomScale = ViewSettings.ZoomScale

    if
        steps <> 0L
        && not (Double.IsNaN zoomScale)
        && not (Double.IsInfinity zoomScale)
        && zoomScale > 0.
    then
        let magnification = Math.Pow(1. / zoomScale, float steps)

        if
            Double.IsNaN magnification
            || Double.IsInfinity magnification
            || magnification <= 0.
        then
            1.
        else
            magnification
    else
        1.
