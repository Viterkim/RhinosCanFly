module RhinosCanFly.Movement

open System
open Rhino
open Rhino.Geometry

let clamp (a: float) (b: float) (x: float) = max a (min b x)

let maximum_orbit_angle_per_frame = Math.PI / 2.
let keyboard_pivot_radians_per_second = Math.PI / 6.
let maximum_pitch_radians = RhinoMath.ToRadians 89.

let camera_basis (direction: Vector3d) (requestedUp: Vector3d) =
    let mutable normalizedDirection = direction

    if not (normalizedDirection.Unitize()) then
        failwith "The viewport has an invalid camera direction."

    let mutable normalizedUp =
        requestedUp
        - normalizedDirection * Vector3d.Multiply(requestedUp, normalizedDirection)

    if not (normalizedUp.Unitize()) then
        let fallbackUp =
            if abs normalizedDirection.Z < 0.9 then
                Vector3d.ZAxis
            else
                Vector3d.YAxis

        normalizedUp <-
            fallbackUp
            - normalizedDirection * Vector3d.Multiply(fallbackUp, normalizedDirection)

        if not (normalizedUp.Unitize()) then
            failwith "The viewport has an invalid camera orientation."

    struct (normalizedDirection, normalizedUp)

let camera_right (camera: CameraState) =
    let mutable right = Vector3d.CrossProduct(camera.direction, camera.up)

    if not (right.Unitize()) then
        failwith "The flight camera has an invalid orientation."

    right

let pitch (camera: CameraState) =
    Math.Asin(clamp -1. 1. camera.direction.Z)

let target_distance (camera: CameraState) =
    let distance = camera.position.DistanceTo camera.target

    if RhinoMath.IsValidDouble distance && distance > RhinoMath.ZeroTolerance then
        distance
    else
        1.

let target_on_camera_axis (location: Point3d) (target: Point3d) (cameraDirection: Vector3d) =
    let mutable direction = cameraDirection

    if not location.IsValid || not (direction.Unitize()) then
        target
    else
        let projectedDistance = Vector3d.Multiply(target - location, direction)

        let distance =
            if
                RhinoMath.IsValidDouble projectedDistance
                && projectedDistance > RhinoMath.ZeroTolerance
            then
                projectedDistance
            else
                let directDistance = location.DistanceTo target

                if
                    RhinoMath.IsValidDouble directDistance
                    && directDistance > RhinoMath.ZeroTolerance
                then
                    directDistance
                else
                    1.

        location + direction * distance

let target_depth (target: Point3d) (camera: CameraState) =
    Vector3d.Multiply(target - camera.position, camera.direction)

let dolly_towards (target: Point3d) (magnification: float) (camera: CameraState) =
    let depth = target_depth target camera

    if
        not (RhinoMath.IsValidDouble magnification)
        || magnification <= RhinoMath.ZeroTolerance
        || magnification = 1.
        || not (RhinoMath.IsValidDouble depth)
        || depth <= RhinoMath.ZeroTolerance
    then
        camera
    else
        let nextDepth = depth / magnification

        if not (RhinoMath.IsValidDouble nextDepth) || nextDepth <= RhinoMath.ZeroTolerance then
            camera
        else
            let forwardDistance = depth - nextDepth
            let translation = camera.direction * forwardDistance

            { camera with
                position = camera.position + translation
                target = camera.target + translation }

[<Struct>]
type MouseAngleDeltas =
    { yaw_delta: float; pitch_delta: float }

let scaled_mouse_angle_deltas
    (config: FlyingMouseConfig)
    (mouseSensitivity: RuntimeMouseSensitivity)
    (multiplier: float)
    (mouseDx: int64)
    (mouseDy: int64)
    =
    let horizontal_sign = if config.x_mode = MouseAxisMode.Inverted then 1. else -1.

    let vertical_sign = if config.y_mode = MouseAxisMode.Inverted then 1. else -1.

    let sensitivity = MouseSensitivity.radians_per_count mouseSensitivity
    let yawDelta = float mouseDx * sensitivity * horizontal_sign * multiplier
    let pitchDelta = float mouseDy * sensitivity * vertical_sign * multiplier

    { yaw_delta = yawDelta
      pitch_delta = pitchDelta }

let clamped_mouse_angle_deltas
    (config: FlyingMouseConfig)
    (mouseSensitivity: RuntimeMouseSensitivity)
    (multiplier: float)
    (mouseDx: int64)
    (mouseDy: int64)
    (camera: CameraState)
    =
    let deltas =
        scaled_mouse_angle_deltas config mouseSensitivity multiplier mouseDx mouseDy

    let currentPitch = pitch camera
    let requestedPitch = currentPitch + deltas.pitch_delta

    let nextPitch =
        if deltas.pitch_delta = 0. then
            currentPitch
        elif currentPitch > maximum_pitch_radians then
            if deltas.pitch_delta < 0. then
                max maximum_pitch_radians requestedPitch
            else
                currentPitch
        elif currentPitch < -maximum_pitch_radians then
            if deltas.pitch_delta > 0. then
                min -maximum_pitch_radians requestedPitch
            else
                currentPitch
        else
            clamp -maximum_pitch_radians maximum_pitch_radians requestedPitch

    { yaw_delta = deltas.yaw_delta
      pitch_delta = nextPitch - currentPitch }

let rotate_vector (axis: Vector3d) (angle: float) (vector: Vector3d) =
    let mutable rotated = vector

    if rotated.Rotate(angle, axis) then rotated else vector

let mouse_look
    (config: FlyingMouseConfig)
    (mouseSensitivity: RuntimeMouseSensitivity)
    (mouseDx: int64)
    (mouseDy: int64)
    (camera: CameraState)
    =
    let rotation =
        clamped_mouse_angle_deltas config mouseSensitivity 1. mouseDx mouseDy camera

    let yawDirection = rotate_vector Vector3d.ZAxis rotation.yaw_delta camera.direction
    let yawUp = rotate_vector Vector3d.ZAxis rotation.yaw_delta camera.up

    let yawedCamera =
        { camera with
            direction = yawDirection
            up = yawUp }

    let right = camera_right yawedCamera
    let requestedDirection = rotate_vector right rotation.pitch_delta yawDirection
    let requestedUp = rotate_vector right rotation.pitch_delta yawUp
    let struct (direction, up) = camera_basis requestedDirection requestedUp

    { camera with
        target = camera.position + direction * target_distance camera
        direction = direction
        up = up }

let mouse_orbit
    (config: FlyingMouseConfig)
    (mouseSensitivity: RuntimeMouseSensitivity)
    (multiplier: float)
    (pivotCenter: Point3d)
    (mouseDx: int64)
    (mouseDy: int64)
    (camera: CameraState)
    =
    let positionOffset = camera.position - pivotCenter
    let targetOffset = camera.target - pivotCenter

    let rotation =
        clamped_mouse_angle_deltas config mouseSensitivity multiplier mouseDx mouseDy camera

    let yawPositionOffset =
        rotate_vector Vector3d.ZAxis rotation.yaw_delta positionOffset

    let yawTargetOffset = rotate_vector Vector3d.ZAxis rotation.yaw_delta targetOffset
    let yawDirection = rotate_vector Vector3d.ZAxis rotation.yaw_delta camera.direction
    let yawUp = rotate_vector Vector3d.ZAxis rotation.yaw_delta camera.up

    let yawedCamera =
        { camera with
            direction = yawDirection
            up = yawUp }

    let right = camera_right yawedCamera

    let rotatedPositionOffset =
        rotate_vector right rotation.pitch_delta yawPositionOffset

    let rotatedTargetOffset = rotate_vector right rotation.pitch_delta yawTargetOffset

    let requestedDirection = rotate_vector right rotation.pitch_delta yawDirection
    let requestedUp = rotate_vector right rotation.pitch_delta yawUp
    let struct (direction, up) = camera_basis requestedDirection requestedUp

    let position = pivotCenter + rotatedPositionOffset
    let target = pivotCenter + rotatedTargetOffset

    { position = position
      target = target
      direction = direction
      up = up }

let mouse_pivot
    (config: FlyingMouseConfig)
    (mouseSensitivity: RuntimeMouseSensitivity)
    (MousePivotMultiplier multiplier: MousePivotMultiplier)
    (pivotCenter: Point3d)
    (mouseDx: int64)
    (mouseDy: int64)
    (camera: CameraState)
    =
    mouse_orbit config mouseSensitivity multiplier pivotCenter mouseDx mouseDy camera

let mouse_pan
    (config: FlyingMouseConfig)
    (mouseSensitivity: RuntimeMouseSensitivity)
    (MousePanMultiplier multiplier: MousePanMultiplier)
    (MousePanUnitsPerRadian unitsPerRadian: MousePanUnitsPerRadian)
    (mouseDx: int64)
    (mouseDy: int64)
    (camera: CameraState)
    =
    let right = camera_right camera

    let deltas =
        scaled_mouse_angle_deltas config mouseSensitivity multiplier mouseDx mouseDy

    let translation =
        right * deltas.yaw_delta * unitsPerRadian
        - camera.up * deltas.pitch_delta * unitsPerRadian

    { camera with
        position = camera.position + translation
        target = camera.target + translation }

let rotate_xy (cosine: float) (sine: float) (offset: Vector3d) =
    Vector3d(offset.X * cosine - offset.Y * sine, offset.X * sine + offset.Y * cosine, offset.Z)

let orbit_angle (requestedAngle: float) =
    clamp -maximum_orbit_angle_per_frame maximum_orbit_angle_per_frame requestedAngle

let orbit_point (pivotCenter: Point3d) (requestedAngle: float) (point: Point3d) =
    if requestedAngle = 0. then
        point
    else
        let angle = orbit_angle requestedAngle
        pivotCenter + rotate_xy (Math.Cos angle) (Math.Sin angle) (point - pivotCenter)

let orbit (pivotCenter: Point3d) (requestedAngle: float) (camera: CameraState) =
    if requestedAngle = 0. then
        camera
    else
        let angle = orbit_angle requestedAngle

        let cosine = Math.Cos angle
        let sine = Math.Sin angle
        let position = pivotCenter + rotate_xy cosine sine (camera.position - pivotCenter)
        let target = pivotCenter + rotate_xy cosine sine (camera.target - pivotCenter)
        let requestedDirection = rotate_vector Vector3d.ZAxis angle camera.direction
        let requestedUp = rotate_vector Vector3d.ZAxis angle camera.up
        let struct (direction, up) = camera_basis requestedDirection requestedUp

        { position = position
          target = target
          direction = direction
          up = up }

[<Struct>]
type MovementStep =
    { camera: CameraState
      translation: Vector3d
      forward_distance: float
      key_pivot_angle: float }

let step
    (config: MovementConfig)
    (verticalSpeedMultiplier: float)
    (input: InputSnapshot)
    (keyPivotTarget: Point3d)
    (dt: float)
    (camera: CameraState)
    =
    let forward = camera.direction
    let right = camera_right camera

    let amount (positive: bool) (negative: bool) =
        (if positive then 1. else 0.) - if negative then 1. else 0.

    let forwardAmount = amount input.forward input.backward
    let rightAmount = amount input.right input.left
    let verticalAmount = amount input.up input.down

    let directionalMovement = forward * forwardAmount + right * rightAmount
    let verticalMovement = Vector3d.ZAxis * verticalAmount
    let unscaledMovement = directionalMovement + verticalMovement
    let unscaledSquareLength = unscaledMovement.SquareLength

    let normalizationScale =
        if config.normalize_diagonal_movement && unscaledSquareLength > 1. then
            1. / Math.Sqrt unscaledSquareLength
        else
            1.

    let movement =
        directionalMovement * normalizationScale
        + verticalMovement * (normalizationScale * verticalSpeedMultiplier)

    let translation = movement * input.move_speed * dt
    let forwardDistance = forwardAmount * normalizationScale * input.move_speed * dt

    let translated =
        { position = camera.position + translation
          target = camera.target + translation
          direction = camera.direction
          up = camera.up }

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
      translation = translation
      forward_distance = forwardDistance
      key_pivot_angle = keyPivotAngle }
