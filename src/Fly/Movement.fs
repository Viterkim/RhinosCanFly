module RhinosCanFly.Movement

open System
open Rhino
open Rhino.Geometry

let clamp (a: float) (b: float) (x: float) = max a (min b x)

let maximum_orbit_angle_per_frame = Math.PI / 2.
let keyboard_pivot_radians_per_second = Math.PI / 6.
let maximum_pitch_radians = RhinoMath.ToRadians 89.

let camera_basis (direction: Vector3d) (requested_up: Vector3d) =
    let mutable normalized_direction = direction

    if not (normalized_direction.Unitize()) then
        failwith "The viewport has an invalid camera direction."

    let mutable normalized_up =
        requested_up
        - normalized_direction * Vector3d.Multiply(requested_up, normalized_direction)

    if not (normalized_up.Unitize()) then
        let fallback_up =
            if abs normalized_direction.Z < 0.9 then
                Vector3d.ZAxis
            else
                Vector3d.YAxis

        normalized_up <-
            fallback_up
            - normalized_direction * Vector3d.Multiply(fallback_up, normalized_direction)

        if not (normalized_up.Unitize()) then
            failwith "The viewport has an invalid camera orientation."

    struct (normalized_direction, normalized_up)

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

let target_on_camera_axis (location: Point3d) (target: Point3d) (camera_direction: Vector3d) =
    let mutable direction = camera_direction

    if not location.IsValid || not (direction.Unitize()) then
        target
    else
        let projected_distance = Vector3d.Multiply(target - location, direction)

        let distance =
            if
                RhinoMath.IsValidDouble projected_distance
                && projected_distance > RhinoMath.ZeroTolerance
            then
                projected_distance
            else
                let direct_distance = location.DistanceTo target

                if
                    RhinoMath.IsValidDouble direct_distance
                    && direct_distance > RhinoMath.ZeroTolerance
                then
                    direct_distance
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
        let next_depth = depth / magnification

        if
            not (RhinoMath.IsValidDouble next_depth)
            || next_depth <= RhinoMath.ZeroTolerance
        then
            camera
        else
            let forward_distance = depth - next_depth
            let translation = camera.direction * forward_distance

            { camera with
                position = camera.position + translation
                target = camera.target + translation }

[<Struct>]
type MouseAngleDeltas =
    { yaw_delta: float; pitch_delta: float }

let mouse_angle_scales
    (x_mode: MouseAxisMode)
    (y_mode: MouseAxisMode)
    (mouse_sensitivity: MouseRadiansPerCount)
    (multiplier: float)
    =
    let horizontal_sign = if x_mode = MouseAxisMode.Inverted then 1. else -1.
    let vertical_sign = if y_mode = MouseAxisMode.Inverted then 1. else -1.
    let sensitivity = MouseSensitivity.radians_per_count mouse_sensitivity

    struct (sensitivity * horizontal_sign * multiplier, sensitivity * vertical_sign * multiplier)

let mouse_angle_deltas
    (x_mode: MouseAxisMode)
    (y_mode: MouseAxisMode)
    (mouse_sensitivity: MouseRadiansPerCount)
    (multiplier: float)
    (mouse_dx: int64)
    (mouse_dy: int64)
    =
    let struct (horizontal_scale, vertical_scale) =
        mouse_angle_scales x_mode y_mode mouse_sensitivity multiplier

    { yaw_delta = float mouse_dx * horizontal_scale
      pitch_delta = float mouse_dy * vertical_scale }

let scaled_mouse_angle_deltas
    (config: FlyingMouseConfig)
    (mouse_sensitivity: MouseRadiansPerCount)
    (multiplier: float)
    (mouse_dx: int64)
    (mouse_dy: int64)
    =
    mouse_angle_deltas config.x_mode config.y_mode mouse_sensitivity multiplier mouse_dx mouse_dy

let clamped_mouse_angle_deltas
    (config: FlyingMouseConfig)
    (mouse_sensitivity: MouseRadiansPerCount)
    (multiplier: float)
    (mouse_dx: int64)
    (mouse_dy: int64)
    (camera: CameraState)
    =
    let deltas =
        scaled_mouse_angle_deltas config mouse_sensitivity multiplier mouse_dx mouse_dy

    let current_pitch = pitch camera
    let requested_pitch = current_pitch + deltas.pitch_delta

    let next_pitch =
        if deltas.pitch_delta = 0. then
            current_pitch
        elif current_pitch > maximum_pitch_radians then
            if deltas.pitch_delta < 0. then
                max -maximum_pitch_radians requested_pitch
            else
                current_pitch
        elif current_pitch < -maximum_pitch_radians then
            if deltas.pitch_delta > 0. then
                min maximum_pitch_radians requested_pitch
            else
                current_pitch
        else
            clamp -maximum_pitch_radians maximum_pitch_radians requested_pitch

    { yaw_delta = deltas.yaw_delta
      pitch_delta = next_pitch - current_pitch }

let rotate_vector (axis: Vector3d) (angle: float) (vector: Vector3d) =
    let mutable rotated = vector

    if rotated.Rotate(angle, axis) then rotated else vector

let screen_yaw_delta (camera: CameraState) (yaw_delta: float) =
    if camera.up.Z < 0. then -yaw_delta else yaw_delta

let mouse_look
    (config: FlyingMouseConfig)
    (mouse_sensitivity: MouseRadiansPerCount)
    (mouse_dx: int64)
    (mouse_dy: int64)
    (camera: CameraState)
    =
    let rotation =
        clamped_mouse_angle_deltas config mouse_sensitivity 1. mouse_dx mouse_dy camera

    let yaw_delta = screen_yaw_delta camera rotation.yaw_delta

    let yaw_direction = rotate_vector Vector3d.ZAxis yaw_delta camera.direction
    let yaw_up = rotate_vector Vector3d.ZAxis yaw_delta camera.up

    let yawed_camera =
        { camera with
            direction = yaw_direction
            up = yaw_up }

    let right = camera_right yawed_camera
    let requested_direction = rotate_vector right rotation.pitch_delta yaw_direction
    let requested_up = rotate_vector right rotation.pitch_delta yaw_up
    let struct (direction, up) = camera_basis requested_direction requested_up

    { camera with
        target = camera.position + direction * target_distance camera
        direction = direction
        up = up }

let mouse_pan
    (config: FlyingMouseConfig)
    (mouse_sensitivity: MouseRadiansPerCount)
    (MousePanMultiplier multiplier: MousePanMultiplier)
    (MousePanUnitsPerRadian units_per_radian: MousePanUnitsPerRadian)
    (mouse_dx: int64)
    (mouse_dy: int64)
    (camera: CameraState)
    =
    let right = camera_right camera

    let deltas =
        scaled_mouse_angle_deltas config mouse_sensitivity multiplier mouse_dx mouse_dy

    let translation =
        right * deltas.yaw_delta * units_per_radian
        - camera.up * deltas.pitch_delta * units_per_radian

    { camera with
        position = camera.position + translation
        target = camera.target + translation }

let rotate_xy (cosine: float) (sine: float) (offset: Vector3d) =
    Vector3d(offset.X * cosine - offset.Y * sine, offset.X * sine + offset.Y * cosine, offset.Z)

let orbit_angle (requested_angle: float) =
    clamp -maximum_orbit_angle_per_frame maximum_orbit_angle_per_frame requested_angle

let orbit_point (pivot_center: Point3d) (requested_angle: float) (point: Point3d) =
    if requested_angle = 0. then
        point
    else
        let angle = orbit_angle requested_angle

        pivot_center
        + rotate_xy (Math.Cos angle) (Math.Sin angle) (point - pivot_center)

let orbit (pivot_center: Point3d) (requested_angle: float) (camera: CameraState) =
    if requested_angle = 0. then
        camera
    else
        let angle = orbit_angle requested_angle

        let cosine = Math.Cos angle
        let sine = Math.Sin angle
        let position = pivot_center + rotate_xy cosine sine (camera.position - pivot_center)
        let target = pivot_center + rotate_xy cosine sine (camera.target - pivot_center)
        let requested_direction = rotate_vector Vector3d.ZAxis angle camera.direction
        let requested_up = rotate_vector Vector3d.ZAxis angle camera.up
        let struct (direction, up) = camera_basis requested_direction requested_up

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
    (vertical_speed_multiplier: float)
    (walking_plane: Plane voption)
    (input: FlightMovementInput)
    (key_pivot_target: Point3d)
    (dt: float)
    (camera: CameraState)
    =
    let struct (forward, right) =
        match walking_plane with
        | ValueNone -> struct (camera.direction, camera_right camera)
        | ValueSome plane ->
            let normal = plane.Normal
            let camera_right = camera_right camera

            let mutable walking_forward =
                camera.direction - normal * Vector3d.Multiply(camera.direction, normal)

            if not (walking_forward.Unitize()) then
                walking_forward <- camera.up - normal * Vector3d.Multiply(camera.up, normal)

                if not (walking_forward.Unitize()) then
                    walking_forward <- plane.YAxis

            let mutable walking_right = Vector3d.CrossProduct(walking_forward, normal)

            if not (walking_right.Unitize()) then
                failwith "The walking CPlane has an invalid orientation."

            if Vector3d.Multiply(walking_right, camera_right) < 0. then
                walking_right <- -walking_right

            struct (walking_forward, walking_right)

    let amount (positive: bool) (negative: bool) =
        (if positive then 1. else 0.) - if negative then 1. else 0.

    let forward_amount = amount input.forward input.backward
    let right_amount = amount input.right input.left
    let vertical_amount = amount input.up input.down

    let directional_movement = forward * forward_amount + right * right_amount
    let vertical_movement = Vector3d.ZAxis * vertical_amount
    let unscaled_movement = directional_movement + vertical_movement
    let unscaled_square_length = unscaled_movement.SquareLength

    let normalization_scale =
        if config.normalize_diagonal_movement && unscaled_square_length > 1. then
            1. / Math.Sqrt unscaled_square_length
        else
            1.

    let movement =
        directional_movement * normalization_scale
        + vertical_movement * (normalization_scale * vertical_speed_multiplier)

    let translation = movement * input.move_speed * dt
    let forward_distance = forward_amount * normalization_scale * input.move_speed * dt

    let translated =
        { position = camera.position + translation
          target = camera.target + translation
          direction = camera.direction
          up = camera.up }

    let key_pivot_amount =
        match FlightInput.key_pivot_direction input with
        | NoKeyPivot -> 0.
        | KeyPivotLeft -> -1.
        | KeyPivotRight -> 1.

    let key_pivot_angle =
        key_pivot_amount
        * keyboard_pivot_radians_per_second
        * config.key_pivot_speed_multiplier
        * dt

    { camera = orbit key_pivot_target key_pivot_angle translated
      translation = translation
      forward_distance = forward_distance
      key_pivot_angle = key_pivot_angle }
