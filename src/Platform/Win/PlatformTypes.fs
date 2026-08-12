namespace RhinosCanFly

open System.Drawing

[<Struct>]
type RootWindow = RootWindow of nativeint

[<Struct>]
type CursorPosition = CursorPosition of Point

type CursorClipLease =
    { previous: Rectangle
      installed: Rectangle
      mutable relinquished: bool }

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
      shift_right_click: ModifiedRightClickMode
      alt_right_click: ModifiedRightClickMode
      exit_binding: KeyBinding option
      exit_on_left: bool
      exit_on_right: bool }
