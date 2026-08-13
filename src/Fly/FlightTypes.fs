namespace RhinosCanFly

open Rhino.Display
open Rhino.Geometry

type FlyState =
    { view: RhinoView
      viewport: RhinoViewport
      config: FlyConfig
      host_identity: FlightHostIdentity
      original_cursor: CursorPosition
      original_camera: CameraSnapshot
      original_lens_length: float
      gumball_pivot_target: Point3d option
      mutable key_pivot_target: Point3d
      mutable key_pivot_direction: KeyPivotDirection
      mutable key_pivot_input_state: KeyPivotInputState
      mutable active_mouse_navigation: ActiveMouseNavigation
      mutable latched_mouse_navigation: MouseNavigationMode
      mutable keyboard_held_mouse_navigation: MouseNavigationMode
      mutable keyboard_pivot_toggle_was_down: bool
      mutable keyboard_pan_toggle_was_down: bool
      mutable exit_reason: FlightExitReason option
      mutable restore_camera_on_exit: bool
      mutable camera: CameraState
      mutable speed: float
      mutable boost_enabled: bool
      mutable boost_was_down: bool
      mutable slow_enabled: bool
      mutable slow_was_down: bool
      mutable speed_increase_was_down: bool
      mutable speed_decrease_was_down: bool
      mutable wheel_remainder: int64 }

module FlyState =
    let is_running (state: FlyState) = Option.isNone state.exit_reason

    let request_exit (reason: FlightExitReason) (state: FlyState) =
        if Option.isNone state.exit_reason then
            state.exit_reason <- Some reason

            if FlightExitReason.restores_camera reason then
                state.restore_camera_on_exit <- true

    let exit_reason (state: FlyState) = state.exit_reason
