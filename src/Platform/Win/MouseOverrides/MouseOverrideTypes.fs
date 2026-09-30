module RhinosCanFly.Platform.Win.MouseOverrideTypes

open System
open System.Collections.Generic
open System.Drawing
open System.Windows.Forms
open RhinosCanFly

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
      pivot_center: Rhino.Geometry.Point3d
      original_target: Rhino.Geometry.Point3d voption }

type GestureNavigation =
    | NoGestureNavigation
    | GestureNavigationActive of GestureNavigationSession

type MouseFlightEntry =
    { owner: GestureOwner
      host: ViewportHostIdentity
      mode: FlightMode
      released: bool }

type ViewLatchSession =
    { host: ViewportHostIdentity
      mode: ViewNavigationMode
      pivot_center: Rhino.Geometry.Point3d
      mutable startup_rollback: (unit -> Result<unit, string>) option
      completion: Action option }

type ViewLatch =
    | NoViewLatch
    | WaitingForRelease of ViewLatchSession
    | ViewLatchActive of ViewLatchSession

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
    { mutable routing: MouseOverrideConfig
      mutable lifecycle: OverrideLifecycle
      mutable gesture_navigation: GestureNavigation
      mutable view_latch: ViewLatch
      mutable pending_flight_entry: MouseFlightEntry option
      pending_side_button_events: LinkedList<SideButtonHookEvent>
      mutable processing_side_buttons: bool
      mutable navigation_revision: int64
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

let empty_routing =
    { actions = MouseActionConfig.disabled
      exit_binding = None
      prepare_navigation =
        fun (host: ViewportHostIdentity) (_: NavigationTargetPoint) (_: ViewNavigationMode) (_: unit -> bool) ->
            Ok(struct (host, Rhino.Geometry.Point3d.Unset))
      retarget =
        fun (_: ViewportHostIdentity) (_: ViewportClientPoint) (_: RetargetMode) (_: unit -> bool) ->
            { source_target = ValueNone
              errors = [] } }

let create_state () =
    { routing = empty_routing
      lifecycle = Resuming
      gesture_navigation = NoGestureNavigation
      view_latch = NoViewLatch
      pending_flight_entry = None
      pending_side_button_events = LinkedList<SideButtonHookEvent>()
      processing_side_buttons = false
      navigation_revision = 0L
      side_button_hook_capture =
        { middle = NotOwned
          mouse4 = NotOwned
          mouse5 = NotOwned }
      navigation_exit_requested = false
      suspension_ids = HashSet<int64>()
      next_suspension_id = 0L
      suspension_cleanup_error = None
      poll_timer = new Timer(Interval = POLL_TIMER_INTERVAL_MILLISECONDS) }
