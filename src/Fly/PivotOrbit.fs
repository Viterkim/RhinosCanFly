module RhinosCanFly.PivotOrbit

open System
open Rhino
open Rhino.Geometry

let full_turn = 2. * Math.PI

let rotate_vector (axis: Vector3d) (angle: float) (vector: Vector3d) =
    let mutable rotated = vector

    if rotated.Rotate(angle, axis) then
        rotated
    else
        failwith "The pivot drag could not rotate the camera."

let validate_scale (name: string) (value: float) =
    if not (RhinoMath.IsValidDouble value) then
        invalidArg name "The pivot mouse scale must be finite."

let validate_center (center: Point3d) (camera: CameraState) =
    if not center.IsValid then
        invalidArg (nameof center) "The pivot center is invalid."

    let radius = center.DistanceTo camera.position

    if not (RhinoMath.IsValidDouble radius) || radius <= RhinoMath.ZeroTolerance then
        invalidArg (nameof center) "The pivot center is too close to the camera."

let reset
    (center: Point3d)
    (camera: CameraState)
    (horizontal_radians_per_count: float)
    (vertical_radians_per_count: float)
    (drag: PivotDragState)
    =
    if not (CameraState.valid camera) then
        invalidArg (nameof camera) "The pivot camera is invalid."

    validate_center center camera
    validate_scale (nameof horizontal_radians_per_count) horizontal_radians_per_count
    validate_scale (nameof vertical_radians_per_count) vertical_radians_per_count

    drag.center <- center
    drag.baseline <- camera
    drag.horizontal_radians_per_count <- horizontal_radians_per_count
    drag.vertical_radians_per_count <- vertical_radians_per_count
    drag.total_dx <- 0L
    drag.total_dy <- 0L

let create
    (center: Point3d)
    (camera: CameraState)
    (horizontal_radians_per_count: float)
    (vertical_radians_per_count: float)
    =
    let drag =
        { center = center
          baseline = camera
          horizontal_radians_per_count = horizontal_radians_per_count
          vertical_radians_per_count = vertical_radians_per_count
          total_dx = 0L
          total_dy = 0L }

    reset center camera horizontal_radians_per_count vertical_radians_per_count drag
    drag

let rebase (camera: CameraState) (drag: PivotDragState) =
    reset drag.center camera drag.horizontal_radians_per_count drag.vertical_radians_per_count drag

let wrapped_angle (counts: int64) (radians_per_count: float) =
    Math.IEEERemainder(float counts * radians_per_count, full_turn)

let rotate_turntable (yaw: float) (yawed_right: Vector3d) (pitch: float) (vector: Vector3d) =
    let yawed = rotate_vector Vector3d.ZAxis yaw vector
    rotate_vector yawed_right pitch yawed

let evaluate (drag: PivotDragState) =
    let yaw = wrapped_angle drag.total_dx drag.horizontal_radians_per_count
    let pitch = wrapped_angle drag.total_dy drag.vertical_radians_per_count
    let baseline_right = Movement.camera_right drag.baseline
    let yawed_right = rotate_vector Vector3d.ZAxis yaw baseline_right

    let position =
        drag.center
        + rotate_turntable yaw yawed_right pitch (drag.baseline.position - drag.center)

    let target =
        drag.center
        + rotate_turntable yaw yawed_right pitch (drag.baseline.target - drag.center)

    let requested_direction =
        rotate_turntable yaw yawed_right pitch drag.baseline.direction

    let requested_up = rotate_turntable yaw yawed_right pitch drag.baseline.up
    let struct (direction, up) = Movement.camera_basis requested_direction requested_up

    let camera =
        { position = position
          target = target
          direction = direction
          up = up }

    if not (CameraState.valid camera) then
        failwith "The pivot drag produced an invalid camera."

    camera

let apply_delta (dx: int64) (dy: int64) (drag: PivotDragState) =
    drag.total_dx <- drag.total_dx + dx
    drag.total_dy <- drag.total_dy + dy
    evaluate drag

let transform_for_flight_movement
    (translation: Vector3d)
    (orbit_center: Point3d)
    (requested_orbit_angle: float)
    (drag: PivotDragState)
    =
    let angle = Movement.orbit_angle requested_orbit_angle
    let cosine = Math.Cos angle
    let sine = Math.Sin angle

    let baseline_position =
        orbit_center
        + Movement.rotate_xy cosine sine (drag.baseline.position + translation - orbit_center)

    let baseline_target =
        orbit_center
        + Movement.rotate_xy cosine sine (drag.baseline.target + translation - orbit_center)

    let center =
        orbit_center
        + Movement.rotate_xy cosine sine (drag.center + translation - orbit_center)

    let requested_direction = Movement.rotate_xy cosine sine drag.baseline.direction
    let requested_up = Movement.rotate_xy cosine sine drag.baseline.up
    let struct (direction, up) = Movement.camera_basis requested_direction requested_up

    let baseline =
        { position = baseline_position
          target = baseline_target
          direction = direction
          up = up }

    if not (CameraState.valid baseline) then
        failwith "Flight movement produced an invalid pivot baseline."

    drag.center <- center
    drag.baseline <- baseline
