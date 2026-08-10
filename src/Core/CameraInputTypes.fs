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

type PivotDirection =
    | NoPivot
    | PivotLeft
    | PivotRight

type PivotInputState =
    | WaitingForNeutralPivotInput
    | PivotInputArmed

type MouseNavigationMode =
    | MouseLook
    | MousePivot of target: Point3d

[<Struct>]
type InputSnapshot =
    { forward: bool
      backward: bool
      left: bool
      right: bool
      up: bool
      down: bool
      pivot_left: bool
      pivot_right: bool
      move_speed: float }
