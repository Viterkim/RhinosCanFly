namespace RhinosCanFly

open Rhino.Display
open Rhino.Geometry

[<CLIMutable>]
type FlyConfigFile =
    { config_version: int
      forward: string
      backward: string
      left: string
      right: string
      up: string
      down: string
      boost_toggle: string
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
      mouse_sensitivity: float
      invert_mouse_x: bool
      invert_mouse_y: bool
      normalize_diagonal_movement: bool
      hide_gumball_while_flying: bool
      save_speed_to_document: bool
      load_speed_from_document: bool
      wheel_changes_speed: bool
      exit_on_mouse_left: bool
      exit_on_mouse_right: bool
      exit_on_mouse_middle: bool
      hijack_right_click_to_enter: bool
      hijack_right_click_during_commands: bool
      commands_do_not_repeat: bool
      mouse_button_overrides_enabled: bool
      mouse4_acts_as_middle: bool
      mouse5_acts_as_middle: bool
      boost_hold_instead_of_toggle: bool
      slow_hold_instead_of_toggle: bool
      vertical_speed_multiplier: float
      lens_length_mm_in_mode: float
      viewport_redraw_mode: string }

type KeyBinding = { virtual_keys: int list }

[<Struct>]
type ConfigMouseSensitivity = ConfigMouseSensitivity of float

[<Struct>]
type RuntimeMouseSensitivity = RuntimeMouseSensitivity of float

type ViewportRedrawMode =
    | Rhino
    | RhinoImmediate
    | NativeWindow

type FlyConfig =
    { forward: KeyBinding
      backward: KeyBinding
      left: KeyBinding
      right: KeyBinding
      up: KeyBinding
      down: KeyBinding
      boost_toggle: KeyBinding
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
      mouse_sensitivity: RuntimeMouseSensitivity
      invert_mouse_x: bool
      invert_mouse_y: bool
      normalize_diagonal_movement: bool
      hide_gumball_while_flying: bool
      save_speed_to_document: bool
      load_speed_from_document: bool
      wheel_changes_speed: bool
      exit_on_mouse_left: bool
      exit_on_mouse_right: bool
      exit_on_mouse_middle: bool
      boost_hold_instead_of_toggle: bool
      slow_hold_instead_of_toggle: bool
      vertical_speed_multiplier: float
      lens_length_mm_in_mode: float
      viewport_redraw_mode: ViewportRedrawMode }

type ConfigLoadResult =
    { config_file: FlyConfigFile
      config: FlyConfig
      messages: string list }

type CameraState =
    { position: Point3d
      yaw: float
      pitch: float }

type InputSnapshot =
    { forward: bool
      backward: bool
      left: bool
      right: bool
      up: bool
      down: bool
      move_speed: float }

type FlyState =
    { view: RhinoView
      viewport: RhinoViewport
      config: FlyConfig
      root_window: nativeint
      original_cursor: System.Drawing.Point
      original_lens_length: float
      mutable running: bool
      mutable camera: CameraState
      mutable speed: float
      mutable boost_enabled: bool
      mutable boost_was_down: bool
      mutable slow_enabled: bool
      mutable slow_was_down: bool
      mutable speed_increase_was_down: bool
      mutable speed_decrease_was_down: bool }
