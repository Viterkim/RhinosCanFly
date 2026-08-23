namespace RhinosCanFly

open Rhino.DocObjects
open Rhino.Geometry

[<Struct>]
type ViewportClientPoint = { x: int; y: int }

[<System.Flags>]
type RawMouseButtonEvents =
    | None = 0
    | LeftDown = 1
    | LeftUp = 2
    | RightDown = 4
    | RightUp = 8
    | Mouse4Down = 16
    | Mouse4Up = 32
    | Mouse5Down = 64
    | Mouse5Up = 128

[<Struct; RequireQualifiedAccess>]
type RoutedMouseAction =
    | Off
    | TogglePivot
    | HoldPivot
    | TogglePan
    | HoldPan
    | Retarget of RetargetMode

module RoutedMouseAction =
    let create (action: MouseGestureAction) (retargetMode: RetargetMode) =
        match action with
        | MouseGestureAction.TogglePivot -> RoutedMouseAction.TogglePivot
        | MouseGestureAction.HoldPivot -> RoutedMouseAction.HoldPivot
        | MouseGestureAction.TogglePan -> RoutedMouseAction.TogglePan
        | MouseGestureAction.HoldPan -> RoutedMouseAction.HoldPan
        | MouseGestureAction.Retarget when retargetMode <> RetargetMode.Off -> RoutedMouseAction.Retarget retargetMode
        | MouseGestureAction.Retarget
        | MouseGestureAction.Off
        | _ -> RoutedMouseAction.Off

    let enabled (action: RoutedMouseAction) =
        match action with
        | RoutedMouseAction.Off -> false
        | RoutedMouseAction.TogglePivot
        | RoutedMouseAction.HoldPivot
        | RoutedMouseAction.TogglePan
        | RoutedMouseAction.HoldPan
        | RoutedMouseAction.Retarget _ -> true

    let toggles_pivot (action: RoutedMouseAction) =
        match action with
        | RoutedMouseAction.TogglePivot -> true
        | RoutedMouseAction.Off
        | RoutedMouseAction.HoldPivot
        | RoutedMouseAction.TogglePan
        | RoutedMouseAction.HoldPan
        | RoutedMouseAction.Retarget _ -> false

    let holds_pivot (action: RoutedMouseAction) =
        match action with
        | RoutedMouseAction.HoldPivot -> true
        | RoutedMouseAction.Off
        | RoutedMouseAction.TogglePivot
        | RoutedMouseAction.TogglePan
        | RoutedMouseAction.HoldPan
        | RoutedMouseAction.Retarget _ -> false

    let toggles_pan (action: RoutedMouseAction) =
        match action with
        | RoutedMouseAction.TogglePan -> true
        | RoutedMouseAction.Off
        | RoutedMouseAction.TogglePivot
        | RoutedMouseAction.HoldPivot
        | RoutedMouseAction.HoldPan
        | RoutedMouseAction.Retarget _ -> false

    let holds_pan (action: RoutedMouseAction) =
        match action with
        | RoutedMouseAction.HoldPan -> true
        | RoutedMouseAction.Off
        | RoutedMouseAction.TogglePivot
        | RoutedMouseAction.HoldPivot
        | RoutedMouseAction.TogglePan
        | RoutedMouseAction.Retarget _ -> false

    let retarget_mode (action: RoutedMouseAction) =
        match action with
        | RoutedMouseAction.Retarget mode -> mode
        | RoutedMouseAction.Off
        | RoutedMouseAction.TogglePivot
        | RoutedMouseAction.HoldPivot
        | RoutedMouseAction.TogglePan
        | RoutedMouseAction.HoldPan -> RetargetMode.Off

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

type CameraSnapshot
    internal
    (
        viewProjection: ViewportInfo,
        target: Point3d,
        projectionKind: ViewProjectionKind,
        perspectiveLensLengthMm: float voption
    ) =
    let mutable disposed = false

    member internal _.view_projection = viewProjection
    member _.target = target
    member _.projection = projectionKind
    member _.perspective_lens_length_mm = perspectiveLensLengthMm
    member _.is_disposed = disposed

    member _.dispose() =
        if not disposed then
            disposed <- true
            viewProjection.Dispose()

module CameraSnapshot =
    let capture (viewport: Rhino.Display.RhinoViewport) =
        let projection = ViewProjectionKind.capture viewport
        let viewProjection = new ViewportInfo(viewport)

        let perspectiveLensLength =
            match projection with
            | ViewProjectionKind.Parallel -> ValueNone
            | ViewProjectionKind.Perspective
            | ViewProjectionKind.TwoPointPerspective -> ValueSome viewProjection.Camera35mmLensLength

        CameraSnapshot(viewProjection, viewport.CameraTarget, projection, perspectiveLensLength)

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
