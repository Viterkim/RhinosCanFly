module RhinosCanFly.FlightState

open Rhino.Display
open Rhino.Geometry

let create (view: RhinoView) (config: FlyConfig) (sessionMode: FlightSessionMode) =
    let viewport = view.ActiveViewport
    let bindings = config.bindings
    let mouseNavigationBindings = bindings.mouse_navigation
    let movement = config.movement
    let behavior = config.behavior

    let gumballPivotTarget =
        let mutable gumballPlane = Plane.Unset

        if
            not behavior.pivot_bindings_ignore_gumball
            && RhinoSettings.rotate_view_around_gumball ()
            && view.Document.GetGumballPlane(&gumballPlane)
        then
            Some gumballPlane.Origin
        else
            None

    let originalCursor =
        match PlatformInput.get_cursor_position () with
        | Ok point -> point
        | Error error -> failwith error

    let struct (yaw, pitch) = Movement.angles_from_direction viewport.CameraDirection

    { view = view
      viewport = viewport
      config = config
      host_identity = PlatformInput.capture_flight_host view
      root_window = PlatformInput.root_window view
      original_cursor = originalCursor
      original_camera = CameraSnapshot.capture viewport
      original_lens_length = viewport.Camera35mmLensLength
      gumball_pivot_target = gumballPivotTarget
      key_pivot_target = viewport.CameraTarget
      key_pivot_direction = NoKeyPivot
      key_pivot_input_state = WaitingForNeutralKeyPivotInput
      active_mouse_navigation = MouseLook
      latched_mouse_navigation = LookNavigation
      keyboard_held_mouse_navigation = LookNavigation
      keyboard_pivot_toggle_was_down = FlightControls.is_optional_down mouseNavigationBindings.pivot.toggle
      keyboard_pan_toggle_was_down = FlightControls.is_optional_down mouseNavigationBindings.pan.toggle
      exit_reason = None
      restore_camera_on_exit = sessionMode.flight_mode = FlightMode.Temporary
      camera =
        { position = viewport.CameraLocation
          target = viewport.CameraTarget
          yaw = yaw
          pitch = pitch }
      speed =
        FlightSpeed.current view.Document behavior.load_speed_from_document movement.speed_range movement.base_speed
      boost_enabled = false
      boost_was_down = FlightControls.is_down bindings.boost
      slow_enabled = false
      slow_was_down = FlightControls.is_down bindings.slow
      speed_increase_was_down = FlightControls.is_optional_down bindings.speed_increase
      speed_decrease_was_down = FlightControls.is_optional_down bindings.speed_decrease
      wheel_remainder = 0L }
