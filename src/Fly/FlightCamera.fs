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

let update_navigation_mode (input: InputAccumulator.State) (state: FlyState) =
    if InputAccumulator.drain_pivot_toggles input % 2 <> 0 then
        state.pivot_latched <- not state.pivot_latched

    let pivotActive =
        state.pivot_latched
        || state.keyboard_pivot_held
        || InputAccumulator.pivot_held input

    state.mouse_navigation <-
        match state.mouse_navigation, pivotActive with
        | MouseLook, true -> MousePivot(pivot_target state.view state.gumball_pivot_target)
        | MousePivot _, false -> MouseLook
        | navigation, _ -> navigation

let apply_mouse_input (input: InputAccumulator.State) (state: FlyState) =
    let struct (dx, dy) = InputAccumulator.drain_mouse input

    if dx = 0L && dy = 0L then
        NoCameraChange
    else
        match state.mouse_navigation with
        | MouseLook ->
            state.camera <- Movement.look state.config.mouse dx dy state.camera
            DirectionChanged
        | MousePivot target ->
            state.camera <- Movement.mouse_pivot state.config.mouse target dx dy state.camera
            PositionAndDirectionChanged

let apply (state: FlyState) (change: CameraChange) =
    let changed =
        match change with
        | NoCameraChange -> false
        | PositionChanged ->
            state.viewport.SetCameraLocation(state.camera.position, true)
            true
        | DirectionChanged ->
            let direction = Movement.direction_from_angles state.camera.yaw state.camera.pitch
            state.viewport.SetCameraDirection(direction, true)
            true
        | PositionAndDirectionChanged ->
            state.viewport.SetCameraLocation(state.camera.position, true)

            let direction = Movement.direction_from_angles state.camera.yaw state.camera.pitch
            state.viewport.SetCameraDirection(direction, true)
            true

    if changed then
        FlightRedraw.redraw state.config.behavior.viewport_redraw_mode state.view

let apply_entry_lens (state: FlyState) =
    let adjustment = state.config.behavior.lens_adjustment

    let forcedOrOriginal =
        adjustment.forced_length_mm |> Option.defaultValue state.original_lens_length

    let lens = forcedOrOriginal + adjustment.delta_mm

    if Option.isSome adjustment.forced_length_mm || adjustment.delta_mm <> 0. then
        if not (RhinoMath.IsValidDouble lens) || lens <= 0. then
            failwith $"The configured lens adjustment produces an invalid lens length: {lens} mm"

        state.viewport.Camera35mmLensLength <- lens
