namespace RhinosCanFly

open System
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
      yaw: float
      pitch: float }

module CameraState =
    let finite (value: float) =
        not (Double.IsNaN value) && not (Double.IsInfinity value)

    let valid (camera: CameraState) =
        camera.position.IsValid
        && camera.target.IsValid
        && finite camera.yaw
        && finite camera.pitch

[<Struct>]
type CameraSnapshot =
    { location: Point3d
      target: Point3d
      up: Vector3d }

module CameraSnapshot =
    let capture (viewport: Rhino.Display.RhinoViewport) =
        { location = viewport.CameraLocation
          target = viewport.CameraTarget
          up = viewport.CameraUp }

    let restore (viewport: Rhino.Display.RhinoViewport) (snapshot: CameraSnapshot) =
        viewport.SetCameraLocations(snapshot.target, snapshot.location)
        viewport.CameraUp <- snapshot.up

type CameraChange =
    | NoCameraChange
    | PositionChanged
    | DirectionChanged
    | PositionAndDirectionChanged

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

type ActiveMouseNavigation =
    | MouseLook
    | MousePivot of target: Point3d
    | MousePan of units_per_radian: MousePanUnitsPerRadian

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
