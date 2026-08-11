module RhinosCanFly.FlightCamera

open Rhino
open Rhino.Display
open Rhino.Geometry

let navigation_target (view: RhinoView) (gumballTarget: Point3d option) =
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
        state.latched_mouse_navigation <- MouseNavigationMode.toggle PivotNavigation state.latched_mouse_navigation

    let requestedNavigation =
        if state.keyboard_held_mouse_navigation <> LookNavigation then
            state.keyboard_held_mouse_navigation
        elif InputAccumulator.pivot_held input then
            PivotNavigation
        else
            state.latched_mouse_navigation

    state.active_mouse_navigation <-
        match requestedNavigation with
        | LookNavigation -> MouseLook
        | PivotNavigation ->
            match state.active_mouse_navigation with
            | MousePivot _ -> state.active_mouse_navigation
            | MouseLook
            | MousePan _ -> MousePivot(navigation_target state.view state.gumball_pivot_target)
        | PanNavigation ->
            match state.active_mouse_navigation with
            | MousePan _ -> state.active_mouse_navigation
            | MouseLook
            | MousePivot _ ->
                let panTarget = navigation_target state.view None
                let targetDistance = state.camera.position.DistanceTo panTarget

                let unitsPerRadian =
                    if
                        RhinoMath.IsValidDouble targetDistance
                        && targetDistance > RhinoMath.ZeroTolerance
                    then
                        targetDistance
                    else
                        1.

                MousePan(MousePanUnitsPerRadian unitsPerRadian)

let apply_mouse_input (input: InputAccumulator.State) (state: FlyState) =
    let struct (dx, dy) = InputAccumulator.drain_mouse input

    if dx = 0L && dy = 0L then
        NoCameraChange
    else
        match state.active_mouse_navigation with
        | MouseLook ->
            state.camera <- Movement.look state.config.mouse dx dy state.camera
            DirectionChanged
        | MousePivot target ->
            state.camera <- Movement.mouse_pivot state.config.mouse target dx dy state.camera
            PositionAndDirectionChanged
        | MousePan unitsPerRadian ->
            state.camera <- Movement.mouse_pan state.config.mouse unitsPerRadian dx dy state.camera
            PositionChanged

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
