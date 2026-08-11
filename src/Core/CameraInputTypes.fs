namespace RhinosCanFly

open Rhino.Geometry

[<Struct>]
type CameraState =
    { position: Point3d
      yaw: float
      pitch: float }

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
        match requested, current with
        | PivotNavigation, PivotNavigation
        | PanNavigation, PanNavigation -> LookNavigation
        | _ -> requested

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
