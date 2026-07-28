module RhinosCanFly.FlightCamera

open Rhino.Display

let apply_mouse_look (input: InputAccumulator.State) (state: FlyState) =
    let dx, dy = InputAccumulator.drain_mouse input

    if dx = 0L && dy = 0L then
        false
    else
        state.camera <- Movement.look state.config dx dy state.camera
        true

let camera_direction (camera: CameraState) =
    Movement.direction_from_angles camera.yaw camera.pitch

let set_direction (viewport: RhinoViewport) (camera: CameraState) =
    viewport.SetCameraDirection(camera_direction camera, true)

let redraw (view: RhinoView) =
#if RHINO9
    match PlatformInput.request_view_redraw view.Handle with
    | Error error -> failwith error
    | Ok() ->
        match PlatformInput.update_view view.Handle with
        | Ok() -> ()
        | Error error -> failwith error
#else
    view.Redraw()
#endif

let apply (state: FlyState) (mouseChanged: bool) (movementChanged: bool) =
    if movementChanged then
        state.viewport.SetCameraLocation(state.camera.position, true)

    if mouseChanged then
        set_direction state.viewport state.camera

    redraw state.view

let apply_entry_lens (state: FlyState) =
    let lens = state.config.lens_length_mm_in_mode

    if lens > 0. then
        state.viewport.Camera35mmLensLength <- lens
