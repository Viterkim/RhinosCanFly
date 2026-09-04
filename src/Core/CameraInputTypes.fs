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
        let mutable target_direction = camera.target - camera.position

        camera.position.IsValid
        && camera.target.IsValid
        && valid_basis camera.direction camera.up
        && target_direction.Unitize()
        && Vector3d.Multiply(target_direction, camera.direction) > 0.999999

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

type CameraSnapshot
    internal
    (
        view_projection: ViewportInfo,
        target: Point3d,
        projection_kind: ViewProjectionKind,
        perspective_lens_length: PerspectiveLensLengthMm voption
    ) =
    let mutable disposed = false

    member internal _.view_projection = view_projection
    member _.target = target
    member _.projection = projection_kind
    member _.perspective_lens_length = perspective_lens_length
    member _.is_disposed = disposed

    member _.dispose() =
        if not disposed then
            disposed <- true
            view_projection.Dispose()

module CameraSnapshot =
    let capture (viewport: Rhino.Display.RhinoViewport) =
        let projection = ViewProjectionKind.capture viewport
        let view_projection = new ViewportInfo(viewport)

        try
            let perspective_lens_length =
                match projection with
                | ViewProjectionKind.Parallel -> ValueNone
                | ViewProjectionKind.Perspective
                | ViewProjectionKind.TwoPointPerspective ->
                    ValueSome(PerspectiveLensLengthMm view_projection.Camera35mmLensLength)

            CameraSnapshot(view_projection, viewport.CameraTarget, projection, perspective_lens_length)
        with _ ->
            view_projection.Dispose()
            reraise ()

    let restore (viewport: Rhino.Display.RhinoViewport) (snapshot: CameraSnapshot) =
        if snapshot.is_disposed then
            failwith "The camera snapshot has already been disposed."

        if not (viewport.SetViewProjection(snapshot.view_projection, false)) then
            failwith "Rhino could not restore the viewport projection."

        viewport.SetCameraTarget(snapshot.target, false)

    let dispose (snapshot: CameraSnapshot) = snapshot.dispose ()

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

[<Struct>]
type InputEffect =
    { view_change: ViewChange
      pointer_rebase_required: bool }

module InputEffect =
    let none =
        { view_change = ViewChange.none
          pointer_rebase_required = false }

    let rebase_pointer (change: ViewChange) =
        { view_change = change
          pointer_rebase_required = true }

    let combine (first: InputEffect) (second: InputEffect) =
        { view_change = ViewChange.combine first.view_change second.view_change
          pointer_rebase_required = first.pointer_rebase_required || second.pointer_rebase_required }

type KeyPivotDirection =
    | NoKeyPivot
    | KeyPivotLeft
    | KeyPivotRight

type KeyPivotInputState =
    | KeyPivotInputArmed
    | KeyPivotInputActive

type MouseNavigationMode =
    | LookNavigation
    | PivotNavigation
    | PanNavigation

module MouseNavigationMode =
    let toggle (requested: MouseNavigationMode) (current: MouseNavigationMode) =
        if requested = current then LookNavigation else requested

[<Struct>]
type MousePanUnitsPerRadian = MousePanUnitsPerRadian of units_per_radian: float

type PivotDragState =
    { mutable center: Point3d
      mutable baseline: CameraState
      mutable horizontal_radians_per_count: float
      mutable vertical_radians_per_count: float
      mutable total_dx: int64
      mutable total_dy: int64 }

[<Struct>]
type ActiveMouseNavigation =
    | MouseLook
    | MousePivot of drag: PivotDragState
    | MousePan of target: Point3d * units_per_radian: MousePanUnitsPerRadian

[<Struct>]
type FlightMovementInput =
    { forward: bool
      backward: bool
      left: bool
      right: bool
      up: bool
      down: bool
      key_pivot_left: bool
      key_pivot_right: bool
      move_speed: float }
