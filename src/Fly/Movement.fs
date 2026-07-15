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

let step (config: FlyConfig) (input: InputSnapshot) (dt: float) (camera: CameraState) =
    let sign = if config.invert_mouse_y then 1. else -1.
    let sensitivity = MouseSensitivity.radians_per_count config.mouse_sensitivity
    let yaw = camera.yaw - float input.mouse_dx * sensitivity
    let limit = RhinoMath.ToRadians 89.

    let pitch =
        clamp -limit limit (camera.pitch + float input.mouse_dy * sensitivity * sign)

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

    { position = camera.position + movement * input.move_speed * dt
      yaw = yaw
      pitch = pitch }
