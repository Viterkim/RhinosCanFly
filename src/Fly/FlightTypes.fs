namespace RhinosCanFly

open Rhino.Display
open Rhino.Geometry

type FlyState =
    { view: RhinoView
      viewport: RhinoViewport
      config: FlyConfig
      host_identity: ViewportHostIdentity
      session_mode: FlightSessionMode
      original_cursor: CursorPosition
      original_camera: CameraSnapshot
      gumball_pivot_target: Point3d option
      mutable key_pivot_target: Point3d
      mutable key_pivot_input_state: KeyPivotInputState
      mutable active_mouse_navigation: ActiveMouseNavigation
      mutable latched_mouse_navigation: MouseNavigationMode
      mutable keyboard_pivot_held: bool
      mutable keyboard_pan_held: bool
      mutable mouse_pivot_hold_buttons: int
      mutable mouse_pan_hold_buttons: int
      mutable projection: ViewProjectionKind
      mutable perspective_projection: ViewProjectionKind
      mutable perspective_lens_length_mm: float
      mutable exit_reason: FlightExitReason option
      mutable restore_camera_on_exit: bool
      mutable camera: CameraState
      mutable speed: float
      mutable boost_enabled: bool
      mutable slow_enabled: bool
      mutable wheel_remainder: int64
      mutable next_host_validation_at: float }

module FlyState =
    let is_running (state: FlyState) = Option.isNone state.exit_reason

    let request_exit (reason: FlightExitReason) (state: FlyState) =
        if Option.isNone state.exit_reason then
            state.exit_reason <- Some reason

            if FlightExitReason.restores_camera reason then
                state.restore_camera_on_exit <- true
