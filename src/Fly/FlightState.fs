module RhinosCanFly.FlightState

open Rhino
open Rhino.Display
open Rhino.Geometry

let create
    (view: RhinoView)
    (host_identity: ViewportHostIdentity)
    (config: FlyConfig)
    (session_mode: FlightSessionMode)
    =
    let viewport = view.ActiveViewport
    let movement = config.movement
    let behavior = config.behavior

    let prioritized_target =
        ViewTarget.prioritized_target behavior.prioritized_target view viewport

    let original_cursor =
        match PlatformInput.get_cursor_position () with
        | Ok point -> point
        | Error error -> failwith error

    let camera_location = viewport.CameraLocation

    let struct (camera_direction, camera_up) =
        Movement.camera_basis viewport.CameraDirection viewport.CameraY

    let camera_target =
        Movement.target_on_camera_axis camera_location viewport.CameraTarget camera_direction

    let captured_camera =
        { position = camera_location
          target = camera_target
          direction = camera_direction
          up = camera_up }

    if not (CameraState.valid captured_camera) then
        failwith "The active viewport has an invalid camera."

    let struct (walking_plane, camera) =
        match session_mode.movement_mode with
        | FreeFlight -> struct (ValueNone, captured_camera)
        | CPlaneWalk eye_height ->
            if not (RhinoMath.IsValidDouble eye_height) || eye_height <= 0. then
                failwith "Walking eye height must be greater than zero."

            let plane = viewport.ConstructionPlane()

            if not plane.IsValid then
                failwith "The active viewport has an invalid CPlane."

            if abs plane.Normal.Z < 0.999 then
                RhinoApp.WriteLine
                    "RhinosCanWalk: the current CPlane is tilted from World XY, so movement may feel weird."

            let height_offset = eye_height - plane.DistanceTo captured_camera.position
            let translation = plane.Normal * height_offset

            struct (ValueSome plane,
                    { captured_camera with
                        position = captured_camera.position + translation
                        target = captured_camera.target + translation })

    let speed =
        FlightSpeed.current view.Document behavior.load_speed_from_document movement.speed_range movement.base_speed

    let original_camera = CameraSnapshot.capture viewport

    let perspective_projection =
        match original_camera.projection with
        | ViewProjectionKind.TwoPointPerspective -> ViewProjectionKind.TwoPointPerspective
        | ViewProjectionKind.Parallel
        | ViewProjectionKind.Perspective -> ViewProjectionKind.Perspective

    let perspective_lens_length =
        match original_camera.perspective_lens_length with
        | ValueSome lens -> lens
        | ValueNone -> behavior.perspective_lens.after_parallel

    { view = view
      viewport = viewport
      config = config
      host_identity = host_identity
      session_mode = session_mode
      walking_plane = walking_plane
      original_cursor = original_cursor
      original_camera = original_camera
      prioritized_target = prioritized_target
      key_pivot_target = camera.target
      key_pivot_input_state = KeyPivotInputArmed
      active_mouse_navigation = MouseLook
      latched_mouse_navigation = LookNavigation
      keyboard_pivot_held = false
      keyboard_pan_held = false
      mouse_pivot_hold_buttons = 0
      mouse_pan_hold_buttons = 0
      projection = original_camera.projection
      perspective_projection = perspective_projection
      perspective_lens_length = perspective_lens_length
      exit_reason = None
      restore_camera_on_exit = session_mode.flight_mode = FlightMode.Temporary
      camera = camera
      speed = speed
      boost_enabled = false
      slow_enabled = false
      wheel_remainder = 0L
      next_host_validation_at = FlightControls.HOST_VALIDATION_INTERVAL_SECONDS }
