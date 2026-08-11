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
    | ExitFlying = 1
    | TogglePivot = 2

type ModifiedRightClickMode =
    | Off = 0
    | Pivot = 1
    | Pan = 2

type RightClickEntryMode =
    | Off = 0
    | EnterFlying = 1
    | EnterFlyingDuringCommands = 2
    | EnterFlyingWhileHeld = 3
    | EnterFlyingWhileHeldDuringCommands = 4

type FlightSessionMode =
    | Persistent
    | WhileRightMouseHeld

type ViewportRedrawMode =
    | Rhino = 0
    | RhinoImmediate = 1
    | NativeWindow = 2

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
      pivot_bindings_ignore_gumball: bool
      save_speed_to_document: bool
      load_speed_from_document: bool
      wheel_speed_mode: MouseWheelSpeedMode
      exit_on_mouse_left: bool
      exit_on_mouse_right: bool
      middle_mouse_while_flying: FlyingMiddleMouseMode
      mouse4_also_while_flying: bool
      mouse5_also_while_flying: bool
      right_click_entry_mode: RightClickEntryMode
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
      viewport_redraw_mode: ViewportRedrawMode }

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
