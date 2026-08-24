module RhinosCanFly.Platform.Win.ViewNavigationTypes

open System
open System.Collections.Generic
open System.Drawing
open System.Windows.Forms
open RhinosCanFly

type RoutingConfig =
    { runtime_enabled: bool
      middle: RoutedMouseAction
      mouse4: RoutedMouseAction
      mouse5: RoutedMouseAction
      right_click_entry: RightClickEntryMode
      default_flight_mode: DefaultFlightMode
      parallel_view_flying: ParallelViewFlying
      parallel_right_click_entry: bool
      shift_right_click: RoutedMouseAction
      alt_right_click: RoutedMouseAction
      ctrl_right_click: RoutedMouseAction
      exit: KeyBinding option
      exit_on_mouse_left: bool
      exit_on_mouse_right: bool
      prepare_navigation: ViewportHostIdentity -> ViewNavigationMode -> Result<ViewportHostIdentity, string>
      retarget: ViewportHostIdentity -> ViewportClientPoint -> RetargetMode -> Result<unit, string> }

type SideButton =
    | Middle
    | Mouse4
    | Mouse5

[<Struct>]
type SideButtonHookEvent =
    | ButtonDown of button: SideButton * host: ViewportHostIdentity * screen_point: Point
    | ButtonUp of button: SideButton

type HookButtonOwnership =
    | NotOwned
    | Owned
    | ReleaseObserved

type SideButtonHookCapture =
    { mutable middle: HookButtonOwnership
      mutable mouse4: HookButtonOwnership
      mutable mouse5: HookButtonOwnership }

[<RequireQualifiedAccess>]
type GestureOwner =
    | ModifiedRightClick
    | Middle
    | Mouse4
    | Mouse5

[<RequireQualifiedAccess>]
type GestureLifetime =
    | Toggle
    | Hold

type GestureNavigationSession =
    { owner: GestureOwner
      host: ViewportHostIdentity
      mode: ViewNavigationMode
      lifetime: GestureLifetime
      original_target: Rhino.Geometry.Point3d voption }

type GestureNavigation =
    | NoGestureNavigation
    | GestureNavigationActive of GestureNavigationSession

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
      mutable gesture_navigation: GestureNavigation
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
    { runtime_enabled = false
      middle = RoutedMouseAction.Off
      mouse4 = RoutedMouseAction.Off
      mouse5 = RoutedMouseAction.Off
      right_click_entry = RightClickEntryMode.Off
      default_flight_mode = DefaultFlightMode.Normal
      parallel_view_flying = ParallelViewFlying.DisabledAll
      parallel_right_click_entry = false
      shift_right_click = RoutedMouseAction.Off
      alt_right_click = RoutedMouseAction.Off
      ctrl_right_click = RoutedMouseAction.Off
      exit = None
      exit_on_mouse_left = false
      exit_on_mouse_right = false
      prepare_navigation = fun (host: ViewportHostIdentity) (_: ViewNavigationMode) -> Ok host
      retarget = fun (_: ViewportHostIdentity) (_: ViewportClientPoint) (_: RetargetMode) -> Ok() }

let create_state () =
    { routing = empty_routing
      lifecycle = Resuming
      gesture_navigation = NoGestureNavigation
      view_latch = NoViewLatch
      pending_side_button_events = Queue<SideButtonHookEvent>(16)
      side_button_hook_capture =
        { middle = NotOwned
          mouse4 = NotOwned
          mouse5 = NotOwned }
      navigation_exit_requested = false
      suspension_ids = HashSet<int64>()
      next_suspension_id = 0L
      suspension_cleanup_error = None
      poll_timer = new Timer(Interval = POLL_TIMER_INTERVAL_MILLISECONDS) }
