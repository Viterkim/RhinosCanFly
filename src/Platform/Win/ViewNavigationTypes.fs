module RhinosCanFly.Platform.Win.ViewNavigationTypes

open System
open System.Collections.Generic
open System.Windows.Forms
open RhinosCanFly

type ViewNavigationMode =
    | Pivot
    | Pan

type SideButtonMode =
    | Disabled
    | Hold
    | Toggle

type RoutingConfig =
    { mouse4: SideButtonMode
      mouse5: SideButtonMode
      shift_right_click: ViewNavigationMode option
      alt_right_click: ViewNavigationMode option
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
      mode: ViewNavigationMode
      started_at: int64
      completion: Action option }

type ViewLatch =
    | NoViewLatch
    | WaitingForRelease of ViewLatchSession
    | PivotActive of ViewLatchSession
    | PanActive of ViewLatchSession

type OverrideLifecycle =
    | Available
    | Suspended
    | Resuming
    | Degraded of error: string
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
      pending_side_button_events: Queue<SideButtonHookEvent>
      side_button_hook_capture: SideButtonHookCapture
      mutable navigation_exit_requested: bool
      suspension_ids: HashSet<int64>
      mutable next_suspension_id: int64
      mutable suspension_cleanup_error: string option
      poll_timer: Timer }

[<Literal>]
let poll_timer_interval_milliseconds = 15

[<Literal>]
let poll_timer_watchdog_interval_milliseconds = 250

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
      lifecycle = Resuming
      mouse4 = Released
      mouse5 = Released
      view_latch = NoViewLatch
      pending_side_button_events = Queue<SideButtonHookEvent>(16)
      side_button_hook_capture = { mouse4 = NotOwned; mouse5 = NotOwned }
      navigation_exit_requested = false
      suspension_ids = HashSet<int64>()
      next_suspension_id = 0L
      suspension_cleanup_error = None
      poll_timer = new Timer(Interval = poll_timer_interval_milliseconds) }
