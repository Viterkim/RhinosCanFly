module RhinosCanFly.Platform.Win.ViewNavigationTypes

open System
open System.Collections.Generic
open System.Windows.Forms
open RhinosCanFly

type ViewNavigationMode =
    | Pivot
    | Pan

type ViewNavigationRequest =
    | StartNavigation of ViewNavigationMode
    | StopNavigation

type RoutingConfig =
    { mouse4: MouseButtonPivotMode
      mouse5: MouseButtonPivotMode
      right_click_entry: RightClickEntryMode
      default_flight_mode: DefaultFlightMode
      shift_right_click: ViewNavigationMode option
      alt_right_click: ViewNavigationMode option
      exit: KeyBinding option
      exit_on_mouse_right: bool }

type SideButton =
    | Mouse4
    | Mouse5

[<Struct>]
type SideButtonHookEvent =
    | ButtonDown of button: SideButton * host: ViewportHostIdentity
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
    | HoldActive of host: ViewportHostIdentity
    | TogglePressed of host: ViewportHostIdentity
    | ToggleLatched of host: ViewportHostIdentity
    | ToggleReleasePressed

type ViewLatchSession =
    { host: ViewportHostIdentity
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
let POLL_TIMER_INTERVAL_MILLISECONDS = 15

[<Literal>]
let POLL_TIMER_WATCHDOG_INTERVAL_MILLISECONDS = 250

[<Literal>]
let TRANSITION_TIMEOUT_SECONDS = 2.

let empty_routing =
    { mouse4 = MouseButtonPivotMode.Off
      mouse5 = MouseButtonPivotMode.Off
      right_click_entry = RightClickEntryMode.Off
      default_flight_mode = DefaultFlightMode.Normal
      shift_right_click = None
      alt_right_click = None
      exit = None
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
      poll_timer = new Timer(Interval = POLL_TIMER_INTERVAL_MILLISECONDS) }
