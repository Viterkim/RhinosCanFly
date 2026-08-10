namespace RhinosCanFly

open Rhino.Display
open Rhino.Geometry

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
      pivot_left: string
      pivot_right: string
      pivot_toggle: string
      pivot_hold: string
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
      pivot_speed_multiplier: float
      mouse_pivot_multiplier: float
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

type KeyBinding = { virtual_keys: int list }

[<Struct>]
type ConfigMouseSensitivity = ConfigMouseSensitivity of float

[<Struct>]
type RuntimeMouseSensitivity = RuntimeMouseSensitivity of float

type LensAdjustment =
    { forced_length_mm: float option
      delta_mm: float }

type FlyConfig =
    { forward: KeyBinding
      backward: KeyBinding
      left: KeyBinding
      right: KeyBinding
      up: KeyBinding
      down: KeyBinding
      pivot_left: KeyBinding
      pivot_right: KeyBinding
      pivot_toggle: KeyBinding option
      pivot_hold: KeyBinding option
      boost: KeyBinding
      slow: KeyBinding
      speed_increase: KeyBinding option
      speed_decrease: KeyBinding option
      exit_key: KeyBinding
      base_speed: float
      minimum_speed: float
      maximum_speed: float
      speed_step_multiplier: float
      boost_multiplier: float
      slow_multiplier: float
      pivot_speed_multiplier: float
      mouse_pivot_multiplier: float
      mouse_sensitivity: RuntimeMouseSensitivity
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
      mouse4_pivot_mode: MouseButtonPivotMode
      mouse5_pivot_mode: MouseButtonPivotMode
      boost_mode: KeyActivationMode
      slow_mode: KeyActivationMode
      vertical_speed_multiplier: float
      lens_adjustment: LensAdjustment
      viewport_redraw_mode: ViewportRedrawMode }

type ConfigLoadResult =
    { config_file: FlyConfigFile
      config: FlyConfig
      messages: string list }

[<Struct>]
type CameraState =
    { position: Point3d
      yaw: float
      pitch: float }

type CameraChange =
    | NoCameraChange
    | PositionChanged
    | DirectionChanged
    | PositionAndDirectionChanged

type PivotDirection =
    | NoPivot
    | PivotLeft
    | PivotRight

type PivotInputState =
    | WaitingForNeutralPivotInput
    | PivotInputArmed

type MouseNavigationMode =
    | MouseLook
    | MousePivot of target: Point3d

[<Struct>]
type InputSnapshot =
    { forward: bool
      backward: bool
      left: bool
      right: bool
      up: bool
      down: bool
      pivot_left: bool
      pivot_right: bool
      move_speed: float }

type FlyState =
    { view: RhinoView
      viewport: RhinoViewport
      config: FlyConfig
      root_window: nativeint
      original_cursor: System.Drawing.Point
      original_lens_length: float
      gumball_pivot_target: Point3d option
      mutable pivot_target: Point3d
      mutable pivot_direction: PivotDirection
      mutable pivot_input_state: PivotInputState
      mutable mouse_navigation: MouseNavigationMode
      mutable pivot_latched: bool
      mutable keyboard_pivot_held: bool
      mutable keyboard_pivot_toggle_was_down: bool
      mutable running: bool
      mutable camera: CameraState
      mutable speed: float
      mutable boost_enabled: bool
      mutable boost_was_down: bool
      mutable slow_enabled: bool
      mutable slow_was_down: bool
      mutable speed_increase_was_down: bool
      mutable speed_decrease_was_down: bool }
