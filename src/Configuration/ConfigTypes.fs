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

type MouseButtonPivotMode =
    | Off = 0
    | Hold = 1
    | Toggle = 2

type FlyingMiddleMouseMode =
    | Off = 0
    | ExitFlight = 1
    | TogglePivot = 2

type ModifiedRightClickMode =
    | Off = 0
    | Pivot = 1
    | Pan = 2

type RightClickEntryMode =
    | Off = 0
    | ClickToFly = 1
    | ClickToFlyDuringCommands = 2
    | HoldToFly = 3
    | HoldToFlyDuringCommands = 4

type FlightMode =
    | Normal = 0
    | Temporary = 1

type DefaultFlightMode =
    | Normal = 0
    | Temporary = 1
    | TemporaryIncludingNavigationCommands = 2

type ViewTargetMode =
    | Off = 0
    | Distance = 1
    | GeometryThenDistance = 2

[<Struct>]
type ViewTargetDistanceMultiplier = ViewTargetDistanceMultiplier of float

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
      exit_on_mouse_left: bool
      exit_on_mouse_right: bool
      middle_mouse_while_flying: FlyingMiddleMouseMode
      mouse4_pivot_in_flight: bool
      mouse5_pivot_in_flight: bool
      right_click_entry_mode: RightClickEntryMode
      default_flight_mode: DefaultFlightMode
      view_target_mode: ViewTargetMode
      view_target_distance_multiplier: float
      set_view_target_on_restored_flights: bool
      commands_do_not_repeat: bool
      mouse4_pivot_mode: MouseButtonPivotMode
      mouse5_pivot_mode: MouseButtonPivotMode
      shift_right_click_mode: ModifiedRightClickMode
      alt_right_click_mode: ModifiedRightClickMode
      boost_mode: KeyActivationMode
      slow_mode: KeyActivationMode
      vertical_speed_multiplier: float
      forced_lens_length_mm: float
      lens_length_delta_mm: float
      viewport_paint_mode: ViewportPaintMode }

[<Struct>]
type ConfigMouseSensitivity = ConfigMouseSensitivity of float

[<Struct>]
type RuntimeMouseSensitivity = RuntimeMouseSensitivity of float

[<Struct>]
type MousePivotMultiplier = MousePivotMultiplier of float

[<Struct>]
type MousePanMultiplier = MousePanMultiplier of float

type LensAdjustment =
    { forced_length_mm: float option
      delta_mm: float }
