namespace RhinosCanFly

open System.Drawing

[<Struct>]
type RootWindow = RootWindow of nativeint

[<Struct>]
type CursorPosition = CursorPosition of Point

[<Struct>]
type ViewWindowHandle = ViewWindowHandle of nativeint

[<Struct>]
type FlightHostIdentity =
    { document_serial_number: uint32
      view_serial_number: uint32
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
      mouse4_pivot_mode: MouseButtonPivotMode
      mouse5_pivot_mode: MouseButtonPivotMode
      mouse4_also_while_flying: bool
      mouse5_also_while_flying: bool }

type MouseOverrideConfig =
    { mouse4: MouseButtonPivotMode
      mouse5: MouseButtonPivotMode
      right_click_entry: RightClickEntryMode
      default_flight_mode: DefaultFlightMode
      shift_right_click: ModifiedRightClickMode
      alt_right_click: ModifiedRightClickMode
      exit_binding: KeyBinding option
      exit_on_left: bool
      exit_on_right: bool }
