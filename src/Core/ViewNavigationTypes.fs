namespace RhinosCanFly

[<Struct>]
type ViewportClientPoint = { x: int; y: int }

type RetargetScope =
    | AllViews = 0
    | OtherViews = 1

type ViewNavigationMode =
    | Pivot
    | Pan

[<RequireQualifiedAccess; Struct>]
type NavigationTargetPoint =
    | ViewCenter
    | ClientPoint of ViewportClientPoint

[<Struct>]
type ViewNavigationMouseConfig =
    { x_mode: MouseAxisMode
      y_mode: MouseAxisMode
      perspective_sensitivity: RuntimeMouseSensitivity
      parallel_sensitivity: RuntimeMouseSensitivity
      perspective_pivot_multiplier: MousePivotMultiplier
      parallel_pivot_multiplier: MousePivotMultiplier
      perspective_pan_multiplier: MousePanMultiplier
      parallel_pan_multiplier: MousePanMultiplier }
