namespace RhinosCanFly

open System
open System.Drawing

[<Struct>]
type RootWindow = RootWindow of nativeint

[<Struct>]
type CursorPosition = CursorPosition of Point

[<Struct>]
type ViewWindowHandle = ViewWindowHandle of nativeint

[<Struct>]
type ViewportHostIdentity =
    { document_serial_number: uint32
      view_serial_number: uint32
      viewport_id: Guid
      view_window: ViewWindowHandle
      root_window: RootWindow }

type CursorClipLease =
    { previous: Rectangle
      installed: Rectangle
      mutable relinquished: bool }

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
              -> Result<struct (ViewportHostIdentity * Rhino.Geometry.Point3d), string>
      retarget: ViewportHostIdentity -> ViewportClientPoint -> RetargetMode -> (unit -> bool) -> ApplicationOutcome }
