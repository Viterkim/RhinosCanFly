namespace RhinosCanFly

open Rhino.DocObjects
open Rhino.Geometry

type FlightExitReason =
    | ExplicitKeepCamera
    | ExplicitRestoreCamera
    | RightMouseReleased
    | FocusLost
    | HostInvalid
    | SessionFailure of error: string

module FlightExitReason =
    let is_explicit (reason: FlightExitReason) =
        match reason with
        | ExplicitKeepCamera
        | ExplicitRestoreCamera
        | RightMouseReleased -> true
        | FocusLost
        | HostInvalid
        | SessionFailure _ -> false

    let skips_background_display (reason: FlightExitReason) =
        match reason with
        | FocusLost
        | HostInvalid
        | SessionFailure _ -> true
        | ExplicitKeepCamera
        | ExplicitRestoreCamera
        | RightMouseReleased -> false

    let restores_camera (reason: FlightExitReason) = reason = ExplicitRestoreCamera

[<Struct>]
type CameraState =
    { position: Point3d
      target: Point3d
      direction: Vector3d
      up: Vector3d }

module CameraState =
    let valid_basis (direction: Vector3d) (up: Vector3d) =
        direction.IsValid
        && up.IsValid
        && abs (direction.SquareLength - 1.) < 0.00000001
        && abs (up.SquareLength - 1.) < 0.00000001
        && abs (Vector3d.Multiply(direction, up)) < 0.00000001

    let valid (camera: CameraState) =
        let mutable targetDirection = camera.target - camera.position

        camera.position.IsValid
        && camera.target.IsValid
        && valid_basis camera.direction camera.up
        && targetDirection.Unitize()
        && Vector3d.Multiply(targetDirection, camera.direction) > 0.999999

[<RequireQualifiedAccess>]
type ViewProjectionKind =
    | Parallel
    | Perspective
    | TwoPointPerspective

module ViewProjectionKind =
    let capture (viewport: Rhino.Display.RhinoViewport) =
        if viewport.IsParallelProjection then
            ViewProjectionKind.Parallel
        elif viewport.IsTwoPointPerspectiveProjection then
            ViewProjectionKind.TwoPointPerspective
        else
            ViewProjectionKind.Perspective

[<Struct>]
type ViewFrustum =
    { left: float
      right: float
      bottom: float
      top: float
      near_distance: float
      far_distance: float }

[<Struct>]
type CameraSnapshot =
    { location: Point3d
      target: Point3d
      up: Vector3d
      projection: ViewProjectionKind
      lens_length_mm: float
      frustum: ViewFrustum voption }

module CameraSnapshot =
    let capture (viewport: Rhino.Display.RhinoViewport) =
        let projection = ViewProjectionKind.capture viewport
        let mutable left = 0.
        let mutable right = 0.
        let mutable bottom = 0.
        let mutable top = 0.
        let mutable nearDistance = 0.
        let mutable farDistance = 0.

        let frustum =
            if viewport.GetFrustum(&left, &right, &bottom, &top, &nearDistance, &farDistance) then
                ValueSome
                    { left = left
                      right = right
                      bottom = bottom
                      top = top
                      near_distance = nearDistance
                      far_distance = farDistance }
            else
                ValueNone

        { location = viewport.CameraLocation
          target = viewport.CameraTarget
          up = viewport.CameraUp
          projection = projection
          lens_length_mm = viewport.Camera35mmLensLength
          frustum = frustum }

    let restore (viewport: Rhino.Display.RhinoViewport) (snapshot: CameraSnapshot) =
        let projectionRestored =
            if ViewProjectionKind.capture viewport = snapshot.projection then
                true
            else
                match snapshot.projection with
                | ViewProjectionKind.Parallel -> viewport.ChangeToParallelProjection true
                | ViewProjectionKind.Perspective ->
                    viewport.ChangeToPerspectiveProjection(true, snapshot.lens_length_mm)
                | ViewProjectionKind.TwoPointPerspective ->
                    viewport.ChangeToTwoPointPerspectiveProjection snapshot.lens_length_mm

        if not projectionRestored then
            failwith "Rhino could not restore the viewport projection."

        viewport.SetCameraLocations(snapshot.target, snapshot.location)
        viewport.CameraUp <- snapshot.up

        if
            snapshot.projection <> ViewProjectionKind.Parallel
            && ValueOption.isNone snapshot.frustum
        then
            viewport.Camera35mmLensLength <- snapshot.lens_length_mm

        match snapshot.frustum with
        | ValueNone -> ()
        | ValueSome frustum ->
            use projection = new ViewportInfo(viewport)

            if
                projection.SetFrustum(
                    frustum.left,
                    frustum.right,
                    frustum.bottom,
                    frustum.top,
                    frustum.near_distance,
                    frustum.far_distance
                )
            then
                if not (viewport.SetViewProjection(projection, false)) then
                    failwith "Rhino could not restore the viewport frustum."
            else
                failwith "Rhino could not restore the viewport frustum."

[<Struct>]
type ViewChange =
    { camera_changed: bool
      parallel_magnification: float }

module ViewChange =
    let none =
        { camera_changed = false
          parallel_magnification = 1. }

    let camera (before: CameraState) (after: CameraState) =
        { camera_changed = before <> after
          parallel_magnification = 1. }

    let combine (first: ViewChange) (second: ViewChange) =
        { camera_changed = first.camera_changed || second.camera_changed
          parallel_magnification = first.parallel_magnification * second.parallel_magnification }

type KeyPivotDirection =
    | NoKeyPivot
    | KeyPivotLeft
    | KeyPivotRight

type KeyPivotInputState =
    | WaitingForNeutralKeyPivotInput
    | KeyPivotInputArmed

type MouseNavigationMode =
    | LookNavigation
    | PivotNavigation
    | PanNavigation

module MouseNavigationMode =
    let toggle (requested: MouseNavigationMode) (current: MouseNavigationMode) =
        if requested = current then LookNavigation else requested

[<Struct>]
type MousePanUnitsPerRadian = MousePanUnitsPerRadian of units_per_radian: float

[<Struct>]
type ActiveMouseNavigation =
    | MouseLook
    | MousePivot of target: Point3d
    | MousePan of target: Point3d * units_per_radian: MousePanUnitsPerRadian

[<Struct>]
type InputSnapshot =
    { forward: bool
      backward: bool
      left: bool
      right: bool
      up: bool
      down: bool
      key_pivot_left: bool
      key_pivot_right: bool
      move_speed: float }
