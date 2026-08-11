module RhinosCanFly.Platform.Win.ViewNavigationTypes

open System.Windows.Forms
open RhinosCanFly

type ViewLatchMode =
    | Pivot
    | Pan

type SideButtonMode =
    | Disabled
    | Hold
    | Toggle

type RoutingConfig =
    { mouse4: SideButtonMode
      mouse5: SideButtonMode
      shift_right_click: ViewLatchMode option
      alt_right_click: ViewLatchMode option
      exit: KeyBinding option
      exit_on_mouse_left: bool
      exit_on_mouse_right: bool }

type SideButton =
    | Mouse4
    | Mouse5

type SideButtonState =
    | Released
    | HoldActive of window: RootWindow
    | TogglePressed of window: RootWindow
    | ToggleLatched of window: RootWindow
    | ToggleReleasePressed

type PendingViewLatch =
    { window: RootWindow
      mode: ViewLatchMode }

type PivotViewLatch =
    { window: RootWindow
      modifiers_down: bool }

type ViewLatch =
    | NoViewLatch
    | WaitingForRelease of PendingViewLatch
    | RetryingPivot of window: RootWindow
    | PivotActive of PivotViewLatch
    | PanActive of RootWindow

type SyntheticShiftState =
    | ShiftReleased
    | ShiftPressed

type OverrideLifecycle =
    | Available
    | Suspended
    | ShutDown

type State =
    { mutable routing: RoutingConfig
      mutable lifecycle: OverrideLifecycle
      mutable mouse4: SideButtonState
      mutable mouse5: SideButtonState
      mutable view_latch: ViewLatch
      mutable synthetic_shift: SyntheticShiftState
      mutable side_button_restart_pending: bool
      mutable middle_mouse_modifiers_down: bool
      poll_timer: Timer }

[<Literal>]
let poll_timer_interval_milliseconds = 15

let empty_routing =
    { mouse4 = Disabled
      mouse5 = Disabled
      shift_right_click = None
      alt_right_click = None
      exit = None
      exit_on_mouse_left = false
      exit_on_mouse_right = false }

let create_state () =
    { routing = empty_routing
      lifecycle = Available
      mouse4 = Released
      mouse5 = Released
      view_latch = NoViewLatch
      synthetic_shift = ShiftReleased
      side_button_restart_pending = false
      middle_mouse_modifiers_down = false
      poll_timer = new Timer(Interval = poll_timer_interval_milliseconds) }
