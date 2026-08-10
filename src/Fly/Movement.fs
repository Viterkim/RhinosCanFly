module RhinosCanFly.Movement

open System
open Rhino
open Rhino.Geometry

let clamp (a: float) (b: float) (x: float) = max a (min b x)

let angles_from_direction (direction: Vector3d) =
    let mutable normalized = direction

    if normalized.Unitize() then
        struct (Math.Atan2(normalized.Y, normalized.X), Math.Asin(clamp -1. 1. normalized.Z))
    else
        struct (0., 0.)

let direction_from_angles (yaw: float) (pitch: float) =
    let cosine = Math.Cos pitch
    Vector3d(cosine * Math.Cos yaw, cosine * Math.Sin yaw, Math.Sin pitch)

let maximum_orbit_angle_per_frame = Math.PI / 2.
let keyboard_pivot_radians_per_second = Math.PI / 6.
let maximum_pitch_radians = RhinoMath.ToRadians 89.

[<Struct>]
type MouseRotation =
    { yaw_delta: float; pitch_delta: float }

let mouse_rotation (config: FlyConfig) (multiplier: float) (mouseDx: int64) (mouseDy: int64) (camera: CameraState) =
    let horizontal_sign =
        if config.mouse_x_mode = MouseAxisMode.Inverted then
            1.
        else
            -1.

    let vertical_sign =
        if config.mouse_y_mode = MouseAxisMode.Inverted then
            1.
        else
            -1.

    let sensitivity = MouseSensitivity.radians_per_count config.mouse_sensitivity
    let yawDelta = float mouseDx * sensitivity * horizontal_sign * multiplier

    let requestedPitch =
        camera.pitch + float mouseDy * sensitivity * vertical_sign * multiplier

    let pitch = clamp -maximum_pitch_radians maximum_pitch_radians requestedPitch

    { yaw_delta = yawDelta
      pitch_delta = pitch - camera.pitch }

let look (config: FlyConfig) (mouseDx: int64) (mouseDy: int64) (camera: CameraState) =
    let rotation = mouse_rotation config 1. mouseDx mouseDy camera

    { camera with
        yaw = camera.yaw + rotation.yaw_delta
        pitch = camera.pitch + rotation.pitch_delta }

let rotate_vector (axis: Vector3d) (angle: float) (vector: Vector3d) =
    let mutable rotated = vector

    if rotated.Rotate(angle, axis) then rotated else vector

let mouse_pivot (config: FlyConfig) (target: Point3d) (mouseDx: int64) (mouseDy: int64) (camera: CameraState) =
    let offset = camera.position - target

    let rotation =
        mouse_rotation config config.mouse_pivot_multiplier mouseDx mouseDy camera

    let direction = direction_from_angles camera.yaw camera.pitch
    let yawOffset = rotate_vector Vector3d.ZAxis rotation.yaw_delta offset
    let yawDirection = rotate_vector Vector3d.ZAxis rotation.yaw_delta direction
    let mutable right = Vector3d.CrossProduct(yawDirection, Vector3d.ZAxis)

    let struct (rotatedOffset, rotatedDirection) =
        if right.Unitize() then
            struct (rotate_vector right rotation.pitch_delta yawOffset,
                    rotate_vector right rotation.pitch_delta yawDirection)
        else
            struct (yawOffset, yawDirection)

    let struct (yaw, pitch) = angles_from_direction rotatedDirection

    { position = target + rotatedOffset
      yaw = yaw
      pitch = pitch }

let orbit (target: Point3d) (requestedAngle: float) (camera: CameraState) =
    if requestedAngle = 0. then
        camera
    else
        let angle =
            clamp -maximum_orbit_angle_per_frame maximum_orbit_angle_per_frame requestedAngle

        let offset = camera.position - target
        let cosine = Math.Cos angle
        let sine = Math.Sin angle

        let rotatedOffset =
            Vector3d(offset.X * cosine - offset.Y * sine, offset.X * sine + offset.Y * cosine, offset.Z)

        let position = target + rotatedOffset
        let direction = direction_from_angles camera.yaw camera.pitch

        let rotatedDirection =
            Vector3d(direction.X * cosine - direction.Y * sine, direction.X * sine + direction.Y * cosine, direction.Z)

        let struct (yaw, pitch) = angles_from_direction rotatedDirection

        { position = position
          yaw = yaw
          pitch = pitch }

let step (config: FlyConfig) (input: InputSnapshot) (pivotTarget: Point3d) (dt: float) (camera: CameraState) =
    let yaw = camera.yaw
    let pitch = camera.pitch

    let forward = direction_from_angles yaw pitch
    let right = Vector3d(Math.Sin yaw, -Math.Cos yaw, 0.)

    let amount (positive: bool) (negative: bool) =
        (if positive then 1. else 0.) - if negative then 1. else 0.

    let mutable forward_amount = amount input.forward input.backward
    let mutable right_amount = amount input.right input.left
    let mutable vertical_amount = amount input.up input.down

    if config.normalize_diagonal_movement then
        let length =
            Math.Sqrt(
                forward_amount * forward_amount
                + right_amount * right_amount
                + vertical_amount * vertical_amount
            )

        if length > 0. then
            forward_amount <- forward_amount / length
            right_amount <- right_amount / length
            vertical_amount <- vertical_amount / length

    let movement =
        forward * forward_amount
        + right * right_amount
        + Vector3d.ZAxis * vertical_amount * config.vertical_speed_multiplier

    let translated =
        { position = camera.position + movement * input.move_speed * dt
          yaw = yaw
          pitch = pitch }

    let pivotAmount =
        match FlightInput.pivot_direction input with
        | NoPivot -> 0.
        | PivotLeft -> -1.
        | PivotRight -> 1.

    let pivotAngle =
        pivotAmount
        * keyboard_pivot_radians_per_second
        * config.pivot_speed_multiplier
        * dt

    orbit pivotTarget pivotAngle translated
