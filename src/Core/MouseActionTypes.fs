namespace RhinosCanFly

type RawMouseButtonEvent =
    | None = 0
    | LeftDown = 1
    | LeftUp = 2
    | RightDown = 3
    | RightUp = 4
    | MiddleDown = 5
    | MiddleUp = 6
    | Mouse4Down = 7
    | Mouse4Up = 8
    | Mouse5Down = 9
    | Mouse5Up = 10

[<Struct>]
type RawMouseButtonTransition =
    { event: RawMouseButtonEvent
      timestamp: int64 }

[<Struct; RequireQualifiedAccess>]
type RoutedMouseAction =
    | Off
    | TogglePivot
    | HoldPivot
    | TogglePan
    | HoldPan
    | Retarget of RetargetMode

module RoutedMouseAction =
    let create (action: MouseGestureAction) (retargetMode: RetargetMode) =
        match action with
        | MouseGestureAction.TogglePivot -> RoutedMouseAction.TogglePivot
        | MouseGestureAction.HoldPivot -> RoutedMouseAction.HoldPivot
        | MouseGestureAction.TogglePan -> RoutedMouseAction.TogglePan
        | MouseGestureAction.HoldPan -> RoutedMouseAction.HoldPan
        | MouseGestureAction.Retarget when retargetMode <> RetargetMode.Off -> RoutedMouseAction.Retarget retargetMode
        | MouseGestureAction.Retarget
        | MouseGestureAction.Off
        | _ -> RoutedMouseAction.Off

    let enabled (action: RoutedMouseAction) =
        match action with
        | RoutedMouseAction.Off -> false
        | RoutedMouseAction.TogglePivot
        | RoutedMouseAction.HoldPivot
        | RoutedMouseAction.TogglePan
        | RoutedMouseAction.HoldPan
        | RoutedMouseAction.Retarget _ -> true

    let toggles_pivot (action: RoutedMouseAction) =
        match action with
        | RoutedMouseAction.TogglePivot -> true
        | RoutedMouseAction.Off
        | RoutedMouseAction.HoldPivot
        | RoutedMouseAction.TogglePan
        | RoutedMouseAction.HoldPan
        | RoutedMouseAction.Retarget _ -> false

    let holds_pivot (action: RoutedMouseAction) =
        match action with
        | RoutedMouseAction.HoldPivot -> true
        | RoutedMouseAction.Off
        | RoutedMouseAction.TogglePivot
        | RoutedMouseAction.TogglePan
        | RoutedMouseAction.HoldPan
        | RoutedMouseAction.Retarget _ -> false

    let toggles_pan (action: RoutedMouseAction) =
        match action with
        | RoutedMouseAction.TogglePan -> true
        | RoutedMouseAction.Off
        | RoutedMouseAction.TogglePivot
        | RoutedMouseAction.HoldPivot
        | RoutedMouseAction.HoldPan
        | RoutedMouseAction.Retarget _ -> false

    let holds_pan (action: RoutedMouseAction) =
        match action with
        | RoutedMouseAction.HoldPan -> true
        | RoutedMouseAction.Off
        | RoutedMouseAction.TogglePivot
        | RoutedMouseAction.HoldPivot
        | RoutedMouseAction.TogglePan
        | RoutedMouseAction.Retarget _ -> false

    let retarget_mode (action: RoutedMouseAction) =
        match action with
        | RoutedMouseAction.Retarget mode -> mode
        | RoutedMouseAction.Off
        | RoutedMouseAction.TogglePivot
        | RoutedMouseAction.HoldPivot
        | RoutedMouseAction.TogglePan
        | RoutedMouseAction.HoldPan -> RetargetMode.Off

[<Struct>]
type OutsideFlightCursorConfig =
    { middle: bool
      mouse4: bool
      mouse5: bool }

type MouseActionConfig =
    { middle: RoutedMouseAction
      mouse4: RoutedMouseAction
      mouse5: RoutedMouseAction
      right_click_entry: RightClickEntryMode
      default_flight_mode: DefaultFlightMode
      viewport_capabilities: ViewportNameList
      right_click_flight_entry: ViewportNameList
      shift_right_click: RoutedMouseAction
      alt_right_click: RoutedMouseAction
      ctrl_right_click: RoutedMouseAction
      exit_on_mouse_left: bool
      exit_on_mouse_right: bool
      outside_flight_cursor: OutsideFlightCursorConfig
      view_navigation_mouse: ViewNavigationMouseConfig }

module MouseActionConfig =
    let disabled =
        { middle = RoutedMouseAction.Off
          mouse4 = RoutedMouseAction.Off
          mouse5 = RoutedMouseAction.Off
          right_click_entry = RightClickEntryMode.Off
          default_flight_mode = DefaultFlightMode.Normal
          viewport_capabilities = ViewportNameList.DisabledAll
          right_click_flight_entry = ViewportNameList.DisabledAll
          shift_right_click = RoutedMouseAction.Off
          alt_right_click = RoutedMouseAction.Off
          ctrl_right_click = RoutedMouseAction.Off
          exit_on_mouse_left = false
          exit_on_mouse_right = false
          outside_flight_cursor =
            { middle = false
              mouse4 = false
              mouse5 = false }
          view_navigation_mouse =
            { x_mode = MouseAxisMode.Normal
              y_mode = MouseAxisMode.Normal
              perspective_sensitivity = RuntimeMouseSensitivity 0.
              parallel_sensitivity = RuntimeMouseSensitivity 0.
              perspective_pivot_multiplier = MousePivotMultiplier 1.
              parallel_pivot_multiplier = MousePivotMultiplier 1.
              perspective_pan_multiplier = MousePanMultiplier 1.
              parallel_pan_multiplier = MousePanMultiplier 1. } }
