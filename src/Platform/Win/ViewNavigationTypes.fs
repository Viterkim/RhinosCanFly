module RhinosCanFly.Platform.Win.ViewNavigationTypes

open System
open System.Collections.Generic
open Eto.Forms
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

[<Struct>]
type SideButtonHookEvent =
    | ButtonDown of button: SideButton * window: RootWindow
    | ButtonUp of button: SideButton

type HookButtonOwnership =
    | NotOwned
    | Owned
    | ReleaseObserved

type SideButtonHookCapture =
    { mutable mouse4: HookButtonOwnership
      mutable mouse5: HookButtonOwnership }

type SideButtonState =
    | Released
    | HoldActive of window: RootWindow
    | TogglePressed of window: RootWindow
    | ToggleLatched of window: RootWindow
    | ToggleReleasePressed

type ViewLatchSession =
    { window: RootWindow
      mode: ViewLatchMode
      started_at: int64
      completion: Action option }

type PivotViewLatch =
    { session: ViewLatchSession
      modifiers_down: bool }

type ViewLatch =
    | NoViewLatch
    | WaitingForRelease of ViewLatchSession
    | RetryingPivot of ViewLatchSession
    | PivotActive of PivotViewLatch
    | PanActive of ViewLatchSession

type SyntheticShiftState =
    | ShiftReleased
    | ShiftPressed
    | ShiftReleasePending

type SyntheticMiddleState =
    | MiddleReleased
    | MiddlePressed
    | MiddleReleasePending

type OverrideLifecycle =
    | Available
    | Suspended
    | ShutDown

type PollRequirement =
    | PollStopped
    | PollWatchdog
    | PollFast

type State =
    { mutable routing: RoutingConfig
      mutable lifecycle: OverrideLifecycle
      mutable mouse4: SideButtonState
      mutable mouse5: SideButtonState
      mutable view_latch: ViewLatch
      mutable synthetic_shift: SyntheticShiftState
      mutable synthetic_middle: SyntheticMiddleState
      mutable side_button_restart_pending: bool
      mutable middle_mouse_modifiers_down: bool
      mutable physical_shift_keys_down: int
      mutable physical_middle_down: bool
      mutable pending_view_completion: Action option
      pending_side_button_events: Queue<SideButtonHookEvent>
      side_button_hook_capture: SideButtonHookCapture
      mutable navigation_exit_requested: bool
      mutable poll_callback_count: int64
      mutable last_poll_duration_ticks: int64
      mutable maximum_poll_duration_ticks: int64
      suspensions: Dictionary<int64, InputSuspensionReason>
      mutable next_suspension_id: int64
      mutable suspension_cleanup_error: string option
      poll_timer: UITimer }

[<Literal>]
let poll_timer_interval_seconds = 0.015

[<Literal>]
let poll_timer_watchdog_interval_seconds = 0.25

[<Literal>]
let transition_timeout_seconds = 2.

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
      synthetic_middle = MiddleReleased
      side_button_restart_pending = false
      middle_mouse_modifiers_down = false
      physical_shift_keys_down = 0
      physical_middle_down = false
      pending_view_completion = None
      pending_side_button_events = Queue<SideButtonHookEvent>()
      side_button_hook_capture = { mouse4 = NotOwned; mouse5 = NotOwned }
      navigation_exit_requested = false
      poll_callback_count = 0L
      last_poll_duration_ticks = 0L
      maximum_poll_duration_ticks = 0L
      suspensions = Dictionary<int64, InputSuspensionReason>()
      next_suspension_id = 0L
      suspension_cleanup_error = None
      poll_timer = new UITimer(Interval = poll_timer_interval_seconds) }
