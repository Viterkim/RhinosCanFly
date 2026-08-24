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

type RawInputConfig =
    { exit_on_mouse_left: bool
      exit_on_mouse_right: bool
      middle_mouse_while_flying: FlyingMiddleMouseMode
      mouse4_action: RoutedMouseAction
      mouse5_action: RoutedMouseAction }

type ViewNavigationMode =
    | Pivot
    | Pan

type MouseOverrideConfig =
    { runtime_enabled: bool
      mouse4: RoutedMouseAction
      mouse5: RoutedMouseAction
      right_click_entry: RightClickEntryMode
      default_flight_mode: DefaultFlightMode
      parallel_view_flying: ParallelViewFlying
      parallel_right_click_entry: bool
      shift_right_click: RoutedMouseAction
      alt_right_click: RoutedMouseAction
      ctrl_right_click: RoutedMouseAction
      exit_binding: KeyBinding option
      exit_on_left: bool
      exit_on_right: bool
      prepare_navigation: ViewportHostIdentity -> ViewNavigationMode -> Result<ViewportHostIdentity, string>
      retarget: ViewportHostIdentity -> ViewportClientPoint -> RetargetMode -> Result<unit, string> }
