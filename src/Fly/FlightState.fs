module RhinosCanFly.FlightState

open Rhino.Display
open Rhino.Geometry

let create (view: RhinoView) (hostIdentity: ViewportHostIdentity) (config: FlyConfig) (sessionMode: FlightSessionMode) =
    let viewport = view.ActiveViewport
    let bindings = config.bindings
    let mouseNavigationBindings = bindings.mouse_navigation
    let movement = config.movement
    let behavior = config.behavior

    let gumballPivotTarget =
        let mutable gumballPlane = Plane.Unset

        if
            behavior.flight_pivot_uses_gumball
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

    let cameraLocation = viewport.CameraLocation

    let struct (cameraDirection, cameraUp) =
        Movement.camera_basis viewport.CameraDirection viewport.CameraY

    let cameraTarget =
        Movement.target_on_camera_axis cameraLocation viewport.CameraTarget cameraDirection

    let camera =
        { position = cameraLocation
          target = cameraTarget
          direction = cameraDirection
          up = cameraUp }

    if not (CameraState.valid camera) then
        failwith "The active viewport has an invalid camera."

    let speed =
        FlightSpeed.current view.Document behavior.load_speed_from_document movement.speed_range movement.base_speed

    let keyboardPivotHeld =
        FlightControls.is_optional_down mouseNavigationBindings.pivot.hold

    let keyboardPanHeld =
        FlightControls.is_optional_down mouseNavigationBindings.pan.hold

    let mousePivotHoldButtons =
        FlightControls.current_mouse_hold_buttons RoutedMouseAction.holds_pivot config.mouse

    let mousePanHoldButtons =
        FlightControls.current_mouse_hold_buttons RoutedMouseAction.holds_pan config.mouse

    let originalCamera = CameraSnapshot.capture viewport

    let perspectiveProjection =
        match originalCamera.projection with
        | ViewProjectionKind.TwoPointPerspective -> ViewProjectionKind.TwoPointPerspective
        | ViewProjectionKind.Parallel
        | ViewProjectionKind.Perspective -> ViewProjectionKind.Perspective

    let perspectiveLensLength =
        match originalCamera.perspective_lens_length_mm with
        | ValueSome lens -> lens
        | ValueNone -> behavior.perspective_lens.after_parallel_mm

    { view = view
      viewport = viewport
      config = config
      host_identity = hostIdentity
      session_mode = sessionMode
      original_cursor = originalCursor
      original_camera = originalCamera
      gumball_pivot_target = gumballPivotTarget
      key_pivot_target = camera.target
      key_pivot_input_state = WaitingForNeutralKeyPivotInput
      active_mouse_navigation = MouseLook
      latched_mouse_navigation = LookNavigation
      keyboard_pivot_held = keyboardPivotHeld
      keyboard_pan_held = keyboardPanHeld
      mouse_pivot_hold_buttons = mousePivotHoldButtons
      mouse_pan_hold_buttons = mousePanHoldButtons
      projection = originalCamera.projection
      perspective_projection = perspectiveProjection
      perspective_lens_length_mm = perspectiveLensLength
      exit_reason = None
      restore_camera_on_exit = sessionMode.flight_mode = FlightMode.Temporary
      camera = camera
      speed = speed
      boost_enabled = false
      slow_enabled = false
      wheel_remainder = 0L
      next_host_validation_at = FlightControls.HOST_VALIDATION_INTERVAL_SECONDS }
