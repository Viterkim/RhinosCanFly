namespace RhinosCanFly

type MouseNavigationModeBindings =
    { toggle: KeyBinding option
      hold: KeyBinding option }

type MouseNavigationBindings =
    { pivot: MouseNavigationModeBindings
      pan: MouseNavigationModeBindings }

type FlightBindings =
    { forward: KeyBinding
      backward: KeyBinding
      left: KeyBinding
      right: KeyBinding
      up: KeyBinding
      down: KeyBinding
      key_pivot_left: KeyBinding
      key_pivot_right: KeyBinding
      mouse_navigation: MouseNavigationBindings
      boost: KeyBinding
      slow: KeyBinding
      speed_increase: KeyBinding option
      speed_decrease: KeyBinding option
      exit_key: KeyBinding
      cancel_flight_and_restore: KeyBinding }

type MovementConfig =
    { base_speed: float
      speed_range: SpeedRange
      speed_step_multiplier: float
      boost_multiplier: float
      slow_multiplier: float
      key_pivot_speed_multiplier: float
      vertical_speed_multiplier: float
      normalize_diagonal_movement: bool
      wheel_speed_mode: MouseWheelSpeedMode
      boost_mode: KeyActivationMode
      slow_mode: KeyActivationMode }

type FlyingMouseConfig =
    { pivot_multiplier: MousePivotMultiplier
      pan_multiplier: MousePanMultiplier
      sensitivity: RuntimeMouseSensitivity
      x_mode: MouseAxisMode
      y_mode: MouseAxisMode
      exit_on_left: bool
      exit_on_right: bool
      middle_button: FlyingMiddleMouseMode
      mouse4_pivot_in_flight: bool
      mouse5_pivot_in_flight: bool
      mouse4_pivot_mode: MouseButtonPivotMode
      mouse5_pivot_mode: MouseButtonPivotMode }

type ViewTargetConfig =
    { mode: ViewTargetMode
      distance_multiplier: ViewTargetDistanceMultiplier
      set_on_restored_flights: bool }

type FlightBehavior =
    { hide_gumball: bool
      flight_pivot_uses_gumball: bool
      view_target: ViewTargetConfig
      save_speed_to_document: bool
      load_speed_from_document: bool
      lens_adjustment: LensAdjustment
      viewport_redraw_mode: ViewportRedrawMode }

type FlyConfig =
    { bindings: FlightBindings
      movement: MovementConfig
      mouse: FlyingMouseConfig
      behavior: FlightBehavior }

type ConfigLoadResult =
    { config_file: FlyConfigFile
      config: FlyConfig
      messages: string list }
