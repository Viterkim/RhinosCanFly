module RhinosCanFly.Movement

open System
open Rhino
open Rhino.Geometry

let clamp (a: float) (b: float) (x: float) = max a (min b x)

let angles_from_direction (direction: Vector3d) =
    let mutable normalized = direction

    if normalized.Unitize() then
        Math.Atan2(normalized.Y, normalized.X), Math.Asin(clamp -1. 1. normalized.Z)
    else
        0., 0.

let direction_from_angles (yaw: float) (pitch: float) =
    let cosine = Math.Cos pitch
    Vector3d(cosine * Math.Cos yaw, cosine * Math.Sin yaw, Math.Sin pitch)

let look (config: FlyConfig) (mouseDx: int64) (mouseDy: int64) (camera: CameraState) =
    let horizontal_sign = if config.invert_mouse_x then 1. else -1.
    let vertical_sign = if config.invert_mouse_y then 1. else -1.
    let sensitivity = MouseSensitivity.radians_per_count config.mouse_sensitivity
    let yaw = camera.yaw + float mouseDx * sensitivity * horizontal_sign
    let limit = RhinoMath.ToRadians 89.

    let pitch =
        clamp -limit limit (camera.pitch + float mouseDy * sensitivity * vertical_sign)

    { camera with yaw = yaw; pitch = pitch }

let orbit (pivotTarget: Point3d) (distance: float) (camera: CameraState) =
    let offset = camera.position - pivotTarget
    let radius = Math.Sqrt(offset.X * offset.X + offset.Y * offset.Y)

    if radius <= RhinoMath.ZeroTolerance || distance = 0. then
        camera
    else
        let angle = distance / radius
        let cosine = Math.Cos angle
        let sine = Math.Sin angle

        let rotatedOffset =
            Vector3d(offset.X * cosine - offset.Y * sine, offset.X * sine + offset.Y * cosine, offset.Z)

        let position = pivotTarget + rotatedOffset
        let direction = direction_from_angles camera.yaw camera.pitch

        let rotatedDirection =
            Vector3d(direction.X * cosine - direction.Y * sine, direction.X * sine + direction.Y * cosine, direction.Z)

        let yaw, pitch = angles_from_direction rotatedDirection

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

    let pivotAmount = float (FlightInput.pivot_direction input)
    orbit pivotTarget (pivotAmount * input.move_speed * config.pivot_speed_multiplier * dt) translated
