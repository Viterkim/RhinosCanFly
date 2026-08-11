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

type MouseNavigationKind =
    | LookNavigation
    | PivotNavigation
    | PanNavigation

module MouseNavigationKind =
    let toggle (requested: MouseNavigationKind) (current: MouseNavigationKind) =
        match requested, current with
        | PivotNavigation, PivotNavigation
        | PanNavigation, PanNavigation -> LookNavigation
        | _ -> requested

[<Struct>]
type MousePanUnitsPerRadian = MousePanUnitsPerRadian of units_per_radian: float

type MouseNavigationMode =
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
      pivot_left: bool
      pivot_right: bool
      move_speed: float }
