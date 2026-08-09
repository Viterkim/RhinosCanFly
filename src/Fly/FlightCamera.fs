module RhinosCanFly.FlightCamera

open Rhino
open Rhino.Display
open Rhino.Geometry

let pivot_target (view: RhinoView) (gumballTarget: Point3d option) =
    match gumballTarget with
    | Some target -> target
    | None ->
        let viewport = view.ActiveViewport
        let cameraLocation = viewport.CameraLocation
        let cameraTarget = viewport.CameraTarget
        let mutable cameraDirection = viewport.CameraDirection
        let visibleBounds = view.Document.Objects.BoundingBoxVisible
        let mutable nearDistance = 0.
        let mutable farDistance = 0.

        if
            cameraDirection.Unitize()
            && visibleBounds.IsValid
            && viewport.GetDepth(visibleBounds, &nearDistance, &farDistance)
            && RhinoMath.IsValidDouble nearDistance
            && RhinoMath.IsValidDouble farDistance
            && farDistance > RhinoMath.ZeroTolerance
        then
            let targetDepth = Vector3d.Multiply(cameraTarget - cameraLocation, cameraDirection)

            if
                not (RhinoMath.IsValidDouble targetDepth)
                || targetDepth < nearDistance
                || targetDepth > farDistance
            then
                cameraLocation + cameraDirection * ((nearDistance + farDistance) / 2.)
            else
                cameraTarget
        else
            cameraTarget

let update_mouse_navigation (input: InputAccumulator.State) (state: FlyState) =
    if InputAccumulator.drain_pivot_toggles input % 2 <> 0 then
        state.mouse_navigation <-
            match state.mouse_navigation with
            | MouseLook -> MousePivot(pivot_target state.view state.gumball_pivot_target)
            | MousePivot _ -> MouseLook

let apply_mouse_input (input: InputAccumulator.State) (state: FlyState) =
    let dx, dy = InputAccumulator.drain_mouse input

    if dx = 0L && dy = 0L then
        None
    else
        match state.mouse_navigation with
        | MouseLook ->
            state.camera <- Movement.look state.config dx dy state.camera
            Some DirectionChanged
        | MousePivot target ->
            state.camera <- Movement.mouse_pivot state.config target dx dy state.camera
            Some PositionAndDirectionChanged

let redraw (mode: ViewportRedrawMode) (view: RhinoView) = FlightRedraw.redraw mode view

let apply (state: FlyState) (change: CameraChange) =
    match change with
    | PositionChanged -> state.viewport.SetCameraLocation(state.camera.position, true)
    | DirectionChanged ->
        let direction = Movement.direction_from_angles state.camera.yaw state.camera.pitch
        state.viewport.SetCameraDirection(direction, true)
    | PositionAndDirectionChanged ->
        state.viewport.SetCameraLocation(state.camera.position, true)

        let direction = Movement.direction_from_angles state.camera.yaw state.camera.pitch
        state.viewport.SetCameraDirection(direction, true)

    redraw state.config.viewport_redraw_mode state.view

let apply_entry_lens (state: FlyState) =
    let lens = state.config.lens_length_mm_in_mode

    if lens > 0. then
        state.viewport.Camera35mmLensLength <- lens
