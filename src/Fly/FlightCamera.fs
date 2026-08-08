module RhinosCanFly.FlightCamera

open Rhino.Display

let apply_mouse_look (input: InputAccumulator.State) (state: FlyState) =
    let dx, dy = InputAccumulator.drain_mouse input

    if dx = 0L && dy = 0L then
        false
    else
        state.camera <- Movement.look state.config dx dy state.camera
        true

let redraw (mode: ViewportRedrawMode) (view: RhinoView) = FlightRedraw.redraw mode view

let apply (state: FlyState) (change: CameraChange) =
    if change.position_changed then
        state.viewport.SetCameraLocation(state.camera.position, true)

    if change.direction_changed then
        let direction = Movement.direction_from_angles state.camera.yaw state.camera.pitch
        state.viewport.SetCameraDirection(direction, true)

    redraw state.config.viewport_redraw_mode state.view

let apply_entry_lens (state: FlyState) =
    let lens = state.config.lens_length_mm_in_mode

    if lens > 0. then
        state.viewport.Camera35mmLensLength <- lens
