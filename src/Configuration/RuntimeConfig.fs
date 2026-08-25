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
      retarget_all_views: KeyBinding option
      retarget_other_views: KeyBinding option
      exit_key: KeyBinding
      cancel_flight_and_restore: KeyBinding
      toggle_projection: KeyBinding option }

type ParallelViewConfig =
    { mouse_sensitivity: RuntimeMouseSensitivity
      mouse_pivot_multiplier: MousePivotMultiplier
      mouse_pan_multiplier: MousePanMultiplier
      zoom_speed_multiplier: float
      up_down_multiplier: float }

type ViewportAccessConfig =
    { capabilities: ViewportNameList
      right_click_flight_entry: ViewportNameList }

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
      wheel_changes_speed_during_flight_navigation: bool
      boost_mode: KeyActivationMode
      slow_mode: KeyActivationMode
      parallel_view: ParallelViewConfig }

type FlyingMouseConfig =
    { pivot_multiplier: MousePivotMultiplier
      pan_multiplier: MousePanMultiplier
      sensitivity: RuntimeMouseSensitivity
      x_mode: MouseAxisMode
      y_mode: MouseAxisMode
      exit_on_left: bool
      exit_on_right: bool
      middle_button: RoutedMouseAction
      mouse4: RoutedMouseAction
      mouse5: RoutedMouseAction }

type RetargetConfig =
    { keyboard_all_views: RetargetMode
      keyboard_other_views: RetargetMode
      shift_right_click: RetargetMode
      alt_right_click: RetargetMode
      ctrl_right_click: RetargetMode
      middle_mouse: RetargetMode
      mouse4: RetargetMode
      mouse5: RetargetMode
      on_pivot: RetargetMode
      on_pan: RetargetMode
      on_flight_exit: RetargetMode
      on_restored_flight_exit: RetargetMode
      perspective_fallback_multiplier: RetargetFallbackMultiplier
      parallel_fallback_multiplier: RetargetFallbackMultiplier
      perspective_zoom_border: float
      parallel_zoom_border: float }

type FlightBehavior =
    { hide_gumball: bool
      flight_pivot_uses_gumball: bool
      retarget: RetargetConfig
      save_speed_to_document: bool
      load_speed_from_document: bool
      perspective_lens: PerspectiveLensConfig
      viewport_paint_mode: ViewportPaintMode }

type FlyConfig =
    { bindings: FlightBindings
      viewport_access: ViewportAccessConfig
      movement: MovementConfig
      mouse: FlyingMouseConfig
      behavior: FlightBehavior }

type ConfigLoadResult =
    { config_file: FlyConfigFile
      config: FlyConfig
      messages: string list }
