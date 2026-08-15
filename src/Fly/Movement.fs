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

let wrap_yaw (yaw: float) = Math.IEEERemainder(yaw, Math.PI * 2.)

let target_distance (camera: CameraState) =
    let distance = camera.position.DistanceTo camera.target

    if RhinoMath.IsValidDouble distance && distance > RhinoMath.ZeroTolerance then
        distance
    else
        1.

[<Struct>]
type MouseAngleDeltas =
    { yaw_delta: float; pitch_delta: float }

let scaled_mouse_angle_deltas (config: FlyingMouseConfig) (multiplier: float) (mouseDx: int64) (mouseDy: int64) =
    let horizontal_sign = if config.x_mode = MouseAxisMode.Inverted then 1. else -1.

    let vertical_sign = if config.y_mode = MouseAxisMode.Inverted then 1. else -1.

    let sensitivity = MouseSensitivity.radians_per_count config.sensitivity
    let yawDelta = float mouseDx * sensitivity * horizontal_sign * multiplier
    let pitchDelta = float mouseDy * sensitivity * vertical_sign * multiplier

    { yaw_delta = yawDelta
      pitch_delta = pitchDelta }

let clamped_mouse_angle_deltas
    (config: FlyingMouseConfig)
    (multiplier: float)
    (mouseDx: int64)
    (mouseDy: int64)
    (camera: CameraState)
    =
    let deltas = scaled_mouse_angle_deltas config multiplier mouseDx mouseDy

    let requestedPitch = camera.pitch + deltas.pitch_delta

    let pitch = clamp -maximum_pitch_radians maximum_pitch_radians requestedPitch

    { yaw_delta = deltas.yaw_delta
      pitch_delta = pitch - camera.pitch }

let look (config: FlyingMouseConfig) (mouseDx: int64) (mouseDy: int64) (camera: CameraState) =
    let rotation = clamped_mouse_angle_deltas config 1. mouseDx mouseDy camera

    let yaw = wrap_yaw (camera.yaw + rotation.yaw_delta)
    let pitch = camera.pitch + rotation.pitch_delta
    let direction = direction_from_angles yaw pitch

    { camera with
        target = camera.position + direction * target_distance camera
        yaw = yaw
        pitch = pitch }

let rotate_vector (axis: Vector3d) (angle: float) (vector: Vector3d) =
    let mutable rotated = vector

    if rotated.Rotate(angle, axis) then rotated else vector

let mouse_pivot
    (config: FlyingMouseConfig)
    (pivotCenter: Point3d)
    (mouseDx: int64)
    (mouseDy: int64)
    (camera: CameraState)
    =
    let positionOffset = camera.position - pivotCenter
    let targetOffset = camera.target - pivotCenter
    let (MousePivotMultiplier multiplier) = config.pivot_multiplier

    let rotation = clamped_mouse_angle_deltas config multiplier mouseDx mouseDy camera

    let direction = direction_from_angles camera.yaw camera.pitch

    let yawPositionOffset =
        rotate_vector Vector3d.ZAxis rotation.yaw_delta positionOffset

    let yawTargetOffset = rotate_vector Vector3d.ZAxis rotation.yaw_delta targetOffset
    let yawDirection = rotate_vector Vector3d.ZAxis rotation.yaw_delta direction
    let mutable right = Vector3d.CrossProduct(yawDirection, Vector3d.ZAxis)

    let struct (rotatedPositionOffset, rotatedTargetOffset) =
        if right.Unitize() then
            struct (rotate_vector right rotation.pitch_delta yawPositionOffset,
                    rotate_vector right rotation.pitch_delta yawTargetOffset)
        else
            struct (yawPositionOffset, yawTargetOffset)

    let position = pivotCenter + rotatedPositionOffset
    let target = pivotCenter + rotatedTargetOffset
    let directionToTarget = target - position
    let struct (yaw, pitch) = angles_from_direction directionToTarget

    { position = position
      target = target
      yaw = yaw
      pitch = pitch }

let mouse_pan
    (config: FlyingMouseConfig)
    (MousePanUnitsPerRadian unitsPerRadian: MousePanUnitsPerRadian)
    (mouseDx: int64)
    (mouseDy: int64)
    (camera: CameraState)
    =
    let (MousePanMultiplier multiplier) = config.pan_multiplier
    let direction = direction_from_angles camera.yaw camera.pitch
    let mutable right = Vector3d.CrossProduct(direction, Vector3d.ZAxis)

    if right.Unitize() then
        let mutable up = Vector3d.CrossProduct(right, direction)

        if up.Unitize() then
            let deltas = scaled_mouse_angle_deltas config multiplier mouseDx mouseDy

            let translation =
                right * deltas.yaw_delta * unitsPerRadian
                - up * deltas.pitch_delta * unitsPerRadian

            { camera with
                position = camera.position + translation
                target = camera.target + translation }
        else
            camera
    else
        camera

let rotate_xy (cosine: float) (sine: float) (offset: Vector3d) =
    Vector3d(offset.X * cosine - offset.Y * sine, offset.X * sine + offset.Y * cosine, offset.Z)

let orbit (pivotCenter: Point3d) (requestedAngle: float) (camera: CameraState) =
    if requestedAngle = 0. then
        camera
    else
        let angle =
            clamp -maximum_orbit_angle_per_frame maximum_orbit_angle_per_frame requestedAngle

        let cosine = Math.Cos angle
        let sine = Math.Sin angle
        let position = pivotCenter + rotate_xy cosine sine (camera.position - pivotCenter)
        let target = pivotCenter + rotate_xy cosine sine (camera.target - pivotCenter)
        let directionToTarget = target - position
        let struct (yaw, pitch) = angles_from_direction directionToTarget

        { position = position
          target = target
          yaw = yaw
          pitch = pitch }

[<Struct>]
type MovementStep =
    { camera: CameraState
      translation: Vector3d }

let step (config: MovementConfig) (input: InputSnapshot) (keyPivotTarget: Point3d) (dt: float) (camera: CameraState) =
    let yaw = camera.yaw
    let pitch = camera.pitch

    let forward = direction_from_angles yaw pitch
    let right = Vector3d(Math.Sin yaw, -Math.Cos yaw, 0.)

    let amount (positive: bool) (negative: bool) =
        (if positive then 1. else 0.) - if negative then 1. else 0.

    let forwardAmount = amount input.forward input.backward
    let rightAmount = amount input.right input.left
    let verticalAmount = amount input.up input.down

    let mutable movement =
        forward * forwardAmount + right * rightAmount + Vector3d.ZAxis * verticalAmount

    if config.normalize_diagonal_movement && movement.SquareLength > 1. then
        movement.Unitize() |> ignore

    movement.Z <- movement.Z * config.vertical_speed_multiplier

    let translation = movement * input.move_speed * dt

    let translated =
        { position = camera.position + translation
          target = camera.target + translation
          yaw = yaw
          pitch = pitch }

    let keyPivotAmount =
        match FlightInput.key_pivot_direction input with
        | NoKeyPivot -> 0.
        | KeyPivotLeft -> -1.
        | KeyPivotRight -> 1.

    let keyPivotAngle =
        keyPivotAmount
        * keyboard_pivot_radians_per_second
        * config.key_pivot_speed_multiplier
        * dt

    { camera = orbit keyPivotTarget keyPivotAngle translated
      translation = translation }
