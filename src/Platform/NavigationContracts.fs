namespace RhinosCanFly

[<Struct>]
type InputSuspensionLease =
    { id: int64
      cleanup_error: string option }

type MouseOverrideConfig =
    { actions: MouseActionConfig
      exit_binding: KeyBinding option
      prepare_navigation:
          ViewportHostIdentity
              -> NavigationTargetPoint
              -> ViewNavigationMode
              -> (unit -> bool)
              -> (Rhino.Geometry.Point3d -> Rhino.Geometry.Point3d -> unit)
              -> Result<struct (ViewportHostIdentity * Rhino.Geometry.Point3d), string>
      retarget: ViewportHostIdentity -> ViewportClientPoint -> RetargetMode -> (unit -> bool) -> ApplicationOutcome }
