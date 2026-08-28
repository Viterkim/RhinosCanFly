module RhinosCanFly.FlightState

open Rhino
open Rhino.Display
open Rhino.Geometry

let create (view: RhinoView) (hostIdentity: ViewportHostIdentity) (config: FlyConfig) (sessionMode: FlightSessionMode) =
    let viewport = view.ActiveViewport
    let movement = config.movement
    let behavior = config.behavior

    let gumballTarget = ViewTarget.gumball_target behavior.use_gumball_as_target view

    let originalCursor =
        match PlatformInput.get_cursor_position () with
        | Ok point -> point
        | Error error -> failwith error

    let cameraLocation = viewport.CameraLocation

    let struct (cameraDirection, cameraUp) =
        Movement.camera_basis viewport.CameraDirection viewport.CameraY

    let cameraTarget =
        Movement.target_on_camera_axis cameraLocation viewport.CameraTarget cameraDirection

    let capturedCamera =
        { position = cameraLocation
          target = cameraTarget
          direction = cameraDirection
          up = cameraUp }

    if not (CameraState.valid capturedCamera) then
        failwith "The active viewport has an invalid camera."

    let struct (walkingPlane, camera) =
        match sessionMode.movement_mode with
        | FreeFlight -> struct (ValueNone, capturedCamera)
        | CPlaneWalk eyeHeight ->
            if not (RhinoMath.IsValidDouble eyeHeight) || eyeHeight <= 0. then
                failwith "Walking eye height must be greater than zero."

            let plane = viewport.ConstructionPlane()

            if not plane.IsValid then
                failwith "The active viewport has an invalid CPlane."

            if abs plane.Normal.Z < 0.999 then
                RhinoApp.WriteLine
                    "RhinosCanWalk: the current CPlane is tilted from World XY, so movement may feel weird."

            let heightOffset = eyeHeight - plane.DistanceTo capturedCamera.position
            let translation = plane.Normal * heightOffset

            struct (ValueSome plane,
                    { capturedCamera with
                        position = capturedCamera.position + translation
                        target = capturedCamera.target + translation })

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
      walking_plane = walkingPlane
      original_cursor = originalCursor
      original_camera = originalCamera
      gumball_target = gumballTarget
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
