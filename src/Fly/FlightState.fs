module RhinosCanFly.FlightState

open Rhino.Display
open Rhino.Geometry

let create (view: RhinoView) (hostIdentity: ViewportHostIdentity) (config: FlyConfig) (sessionMode: FlightSessionMode) =
    let viewport = view.ActiveViewport
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

    let originalCamera = CameraSnapshot.capture viewport

    let perspectiveProjection =
        match originalCamera.projection with
        | ViewProjectionKind.TwoPointPerspective -> ViewProjectionKind.TwoPointPerspective
        | ViewProjectionKind.Parallel
        | ViewProjectionKind.Perspective -> ViewProjectionKind.Perspective

    let perspectiveLensLength =
        match originalCamera.perspective_lens_length with
        | ValueSome lens -> lens
        | ValueNone -> behavior.perspective_lens.after_parallel

    { view = view
      viewport = viewport
      config = config
      host_identity = hostIdentity
      session_mode = sessionMode
      original_cursor = originalCursor
      original_camera = originalCamera
      gumball_pivot_target = gumballPivotTarget
      key_pivot_target = camera.target
      key_pivot_input_state = KeyPivotInputArmed
      active_mouse_navigation = MouseLook
      latched_mouse_navigation = LookNavigation
      keyboard_pivot_held = false
      keyboard_pan_held = false
      mouse_pivot_hold_buttons = 0
      mouse_pan_hold_buttons = 0
      projection = originalCamera.projection
      perspective_projection = perspectiveProjection
      perspective_lens_length = perspectiveLensLength
      exit_reason = None
      restore_camera_on_exit = sessionMode.flight_mode = FlightMode.Temporary
      camera = camera
      speed = speed
      boost_enabled = false
      slow_enabled = false
      wheel_remainder = 0L
      next_host_validation_at = FlightControls.HOST_VALIDATION_INTERVAL_SECONDS }
