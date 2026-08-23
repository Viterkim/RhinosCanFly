namespace RhinosCanFly

type KeyActivationMode =
    | Toggle = 0
    | Hold = 1

type MouseAxisMode =
    | Normal = 0
    | Inverted = 1

type MouseWheelSpeedMode =
    | Off = 0
    | Normal = 1
    | Reversed = 2

type MouseGestureAction =
    | Off = 0
    | TogglePivot = 1
    | HoldPivot = 2
    | TogglePan = 3
    | HoldPan = 4
    | Retarget = 5

type FlyingMiddleMouseMode =
    | Off = 0
    | ExitFlight = 1
    | TogglePivot = 2

type RightClickEntryMode =
    | Off = 0
    | ClickToFly = 1
    | ClickToFlyDuringCommands = 2
    | HoldToFly = 3
    | HoldToFlyDuringCommands = 4

type ParallelViewFlyingMode =
    | DisabledAll = 0
    | EnabledAll = 1
    | EnabledSome = 2
    | DisabledSome = 3

[<CLIMutable>]
type ParallelViewFlyingFile =
    { mode: ParallelViewFlyingMode
      viewports: string array }

type ParallelViewFlying =
    | EnabledAll
    | EnabledSome of viewports: string array
    | DisabledSome of viewports: string array
    | DisabledAll

module ParallelViewFlying =
    let listed (viewportName: string) (viewports: string array) =
        viewports
        |> Array.exists (fun (configured: string) ->
            System.String.Equals(configured, viewportName, System.StringComparison.OrdinalIgnoreCase))

    let allows (viewportName: string) (mode: ParallelViewFlying) =
        match mode with
        | EnabledAll -> true
        | EnabledSome viewports -> listed viewportName viewports
        | DisabledSome viewports -> not (listed viewportName viewports)
        | DisabledAll -> false

    let has_allowed_viewports (mode: ParallelViewFlying) =
        match mode with
        | EnabledAll
        | DisabledSome _ -> true
        | EnabledSome viewports -> viewports.Length > 0
        | DisabledAll -> false

type FlightMode =
    | Normal = 0
    | Temporary = 1

type DefaultFlightMode =
    | Normal = 0
    | Temporary = 1
    | TemporaryIncludingNavigationCommands = 2

type RetargetMode =
    | Off = 0
    | Distance = 1
    | GeometryThenDistance = 2
    | ObjectCenterThenDistance = 3
    | TargetThenDistance = 4

[<Struct>]
type RetargetFallbackMultiplier = RetargetFallbackMultiplier of float

module DefaultFlightMode =
    let flight_mode (mode: DefaultFlightMode) =
        match mode with
        | DefaultFlightMode.Temporary
        | DefaultFlightMode.TemporaryIncludingNavigationCommands -> FlightMode.Temporary
        | DefaultFlightMode.Normal
        | _ -> FlightMode.Normal

    let restores_navigation_commands (mode: DefaultFlightMode) =
        mode = DefaultFlightMode.TemporaryIncludingNavigationCommands

type FlightLifetime =
    | UntilExit
    | WhileRightMouseHeld

[<Struct>]
type FlightSessionMode =
    { lifetime: FlightLifetime
      flight_mode: FlightMode }

module FlightSessionMode =
    let until_exit (flightMode: FlightMode) =
        { lifetime = FlightLifetime.UntilExit
          flight_mode = flightMode }

    let while_right_mouse_held (flightMode: FlightMode) =
        { lifetime = FlightLifetime.WhileRightMouseHeld
          flight_mode = flightMode }

type ViewportPaintMode =
    | Immediate = 0
    | Queued = 1

[<CLIMutable>]
type FlyConfigFile =
    { config_version: int
      enabled: bool
      forward: string
      backward: string
      left: string
      right: string
      up: string
      down: string
      key_pivot_left: string
      key_pivot_right: string
      pivot_toggle: string
      pivot_hold: string
      pan_toggle: string
      pan_hold: string
      boost: string
      slow: string
      speed_increase: string
      speed_decrease: string
      exit_key: string
      cancel_flight_and_restore: string
      toggle_projection: string
      base_speed: float
      minimum_speed: float
      maximum_speed: float
      speed_step_multiplier: float
      boost_multiplier: float
      slow_multiplier: float
      key_pivot_speed_multiplier: float
      mouse_pivot_multiplier: float
      mouse_pan_multiplier: float
      mouse_sensitivity: float
      mouse_x_mode: MouseAxisMode
      mouse_y_mode: MouseAxisMode
      normalize_diagonal_movement: bool
      hide_gumball_while_flying: bool
      flight_pivot_uses_gumball: bool
      save_speed_to_document: bool
      load_speed_from_document: bool
      wheel_speed_mode: MouseWheelSpeedMode
      wheel_changes_speed_during_flight_navigation: bool
      exit_on_mouse_left: bool
      exit_on_mouse_right: bool
      middle_mouse_while_flying: FlyingMiddleMouseMode
      mouse4_action_while_flying: bool
      mouse5_action_while_flying: bool
      right_click_entry_mode: RightClickEntryMode
      default_flight_mode: DefaultFlightMode
      shift_right_click_retarget: RetargetMode
      alt_right_click_retarget: RetargetMode
      ctrl_right_click_retarget: RetargetMode
      mouse4_retarget: RetargetMode
      mouse5_retarget: RetargetMode
      retarget_on_pivot: RetargetMode
      retarget_on_pan: RetargetMode
      retarget_on_flight_exit: RetargetMode
      retarget_on_restored_flight_exit: RetargetMode
      perspective_retarget_fallback_multiplier: float
      parallel_retarget_fallback_multiplier: float
      commands_do_not_repeat: bool
      mouse4_action: MouseGestureAction
      mouse5_action: MouseGestureAction
      shift_right_click_action: MouseGestureAction
      alt_right_click_action: MouseGestureAction
      ctrl_right_click_action: MouseGestureAction
      boost_mode: KeyActivationMode
      slow_mode: KeyActivationMode
      vertical_speed_multiplier: float
      parallel_view_flying: ParallelViewFlyingFile
      parallel_mouse_sensitivity: float
      parallel_mouse_pivot_multiplier: float
      parallel_mouse_pan_multiplier: float
      parallel_zoom_speed_multiplier: float
      parallel_up_down_multiplier: float
      perspective_lens_length_after_parallel_mm: float
      forced_perspective_lens_length_on_flight_start_mm: float
      perspective_lens_length_delta_during_flight_mm: float
      viewport_paint_mode: ViewportPaintMode }

[<Struct>]
type ConfigMouseSensitivity = ConfigMouseSensitivity of float

[<Struct>]
type RuntimeMouseSensitivity = RuntimeMouseSensitivity of float

[<Struct>]
type MousePivotMultiplier = MousePivotMultiplier of float

[<Struct>]
type MousePanMultiplier = MousePanMultiplier of float

type PerspectiveLensConfig =
    { after_parallel_mm: float
      forced_on_flight_start_mm: float option
      delta_during_flight_mm: float }
