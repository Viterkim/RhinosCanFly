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
      middle_mouse_action: RoutedMouseAction
      mouse4_action: RoutedMouseAction
      mouse5_action: RoutedMouseAction }

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

[<Struct>]
type OutsideFlightCursorConfig =
    { middle: bool
      mouse4: bool
      mouse5: bool }

type MouseOverrideConfig =
    { runtime_enabled: bool
      middle: RoutedMouseAction
      mouse4: RoutedMouseAction
      mouse5: RoutedMouseAction
      right_click_entry: RightClickEntryMode
      default_flight_mode: DefaultFlightMode
      viewport_capabilities: ViewportNameList
      right_click_flight_entry: ViewportNameList
      shift_right_click: RoutedMouseAction
      alt_right_click: RoutedMouseAction
      ctrl_right_click: RoutedMouseAction
      exit_binding: KeyBinding option
      exit_on_left: bool
      exit_on_right: bool
      outside_flight_cursor: OutsideFlightCursorConfig
      view_navigation_mouse: ViewNavigationMouseConfig
      prepare_navigation:
          ViewportHostIdentity -> NavigationTargetPoint -> ViewNavigationMode -> Result<ViewportHostIdentity, string>
      retarget: ViewportHostIdentity -> ViewportClientPoint -> RetargetMode -> Result<unit, string> }
