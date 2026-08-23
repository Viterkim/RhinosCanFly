module RhinosCanFly.FlightCamera

open System
open Rhino
open Rhino.ApplicationSettings
open Rhino.Display
open Rhino.Geometry

let fallback_navigation_target (state: FlyState) =
    let viewport = state.viewport
    let cameraLocation = viewport.CameraLocation
    let cameraTarget = viewport.CameraTarget
    let mutable cameraDirection = viewport.CameraDirection

    if ViewTarget.target_is_in_front viewport cameraTarget then
        cameraTarget
    else
        if not (cameraDirection.Unitize()) then
            cameraDirection <- state.camera.direction

        let distance =
            if RhinoMath.IsValidDouble state.speed && state.speed > RhinoMath.ZeroTolerance then
                state.speed
            else
                1.

        cameraLocation + cameraDirection * distance

let navigation_target (state: FlyState) (mode: ViewNavigationMode) (gumballTarget: Point3d option) =
    let viewport = state.viewport

    let retargetMode =
        match mode with
        | ViewNavigationMode.Pivot -> state.config.behavior.retarget.on_pivot
        | ViewNavigationMode.Pan -> state.config.behavior.retarget.on_pan

    match gumballTarget with
    | Some target when ViewTarget.target_is_in_front viewport target -> target
    | Some _
    | None ->
        match
            ViewTarget.selected_target state.config.behavior.retarget retargetMode state.speed state.view viewport
        with
        | Some target when ViewTarget.target_is_in_front viewport target -> target
        | Some _
        | None -> fallback_navigation_target state

let pan_units_per_radian (target: Point3d) (camera: CameraState) =
    let depth = Movement.target_depth target camera

    if RhinoMath.IsValidDouble depth && depth > RhinoMath.ZeroTolerance then
        MousePanUnitsPerRadian depth
    else
        MousePanUnitsPerRadian 1.

let sync_camera_from_viewport (state: FlyState) =
    let viewport = state.viewport
    let position = viewport.CameraLocation

    let struct (direction, up) =
        Movement.camera_basis viewport.CameraDirection viewport.CameraY

    let target = Movement.target_on_camera_axis position viewport.CameraTarget direction

    let camera =
        { position = position
          target = target
          direction = direction
          up = up }

    if not (CameraState.valid camera) then
        state.restore_camera_on_exit <- true
        failwith "Projection conversion produced an invalid camera."

    state.camera <- camera

    match state.active_mouse_navigation with
    | MousePan(panTarget, _) ->
        state.active_mouse_navigation <- MousePan(panTarget, pan_units_per_radian panTarget camera)
    | MouseLook
    | MousePivot _ -> ()

let redraw (state: FlyState) =
    state.view.Redraw()

    if state.config.behavior.viewport_paint_mode = ViewportPaintMode.Immediate then
        PlatformInput.update_window state.view

let toggle_projection (state: FlyState) =
    let viewport = state.viewport

    if
        state.projection = ViewProjectionKind.Parallel
        || ParallelViewFlying.allows viewport.Name state.config.movement.parallel_view.flying
    then

        let conversion =
            if state.projection = ViewProjectionKind.Parallel then
                let lens = state.perspective_lens_length_mm
                let targetDistance = Movement.target_distance state.camera

                match state.perspective_projection with
                | ViewProjectionKind.TwoPointPerspective ->
                    struct (ViewProjectionKind.TwoPointPerspective,
                            viewport.ChangeToTwoPointPerspectiveProjection(targetDistance, viewport.CameraY, lens))
                | ViewProjectionKind.Parallel
                | ViewProjectionKind.Perspective ->
                    struct (ViewProjectionKind.Perspective,
                            viewport.ChangeToPerspectiveProjection(targetDistance, false, lens))
            else
                state.perspective_projection <- state.projection
                struct (ViewProjectionKind.Parallel, viewport.ChangeToParallelProjection false)

        let struct (nextProjection, changed) = conversion

        if not changed then
            state.restore_camera_on_exit <- true
            failwith "Rhino could not change the viewport projection."

        state.projection <- nextProjection
        sync_camera_from_viewport state
        state.wheel_remainder <- 0L
        redraw state

let apply_retarget_request (mode: RetargetMode) (state: FlyState) =
    if mode = RetargetMode.Off then
        ViewChange.none
    else
        match
            ViewTarget.selected_selection_at
                state.config.behavior.retarget
                mode
                state.speed
                state.view
                state.viewport
                (ViewTarget.viewport_center state.viewport)
        with
        | None -> ViewChange.none
        | Some selection ->
            NavigationTarget.apply_selection state.config.behavior.retarget mode state.speed selection state.view
            sync_camera_from_viewport state

            match state.active_mouse_navigation with
            | MousePivot _ -> state.active_mouse_navigation <- MousePivot selection.target
            | MousePan _ ->
                state.active_mouse_navigation <-
                    MousePan(selection.target, pan_units_per_radian selection.target state.camera)
            | MouseLook -> ()

            ViewChange.none

let update_navigation_mode (input: InputAccumulator.State) (state: FlyState) =
    if InputAccumulator.drain_pivot_toggles input % 2 <> 0 then
        state.latched_mouse_navigation <- MouseNavigationMode.toggle PivotNavigation state.latched_mouse_navigation

    if InputAccumulator.drain_pan_toggles input % 2 <> 0 then
        state.latched_mouse_navigation <- MouseNavigationMode.toggle PanNavigation state.latched_mouse_navigation

    let retargetChange =
        apply_retarget_request (InputAccumulator.drain_retarget_request input) state

    let requestedNavigation =
        if state.keyboard_held_mouse_navigation <> LookNavigation then
            state.keyboard_held_mouse_navigation
        elif InputAccumulator.pan_held input then
            PanNavigation
        elif InputAccumulator.pivot_held input then
            PivotNavigation
        else
            state.latched_mouse_navigation

    let previousNavigation =
        match state.active_mouse_navigation with
        | MouseLook -> LookNavigation
        | MousePivot _ -> PivotNavigation
        | MousePan _ -> PanNavigation

    state.active_mouse_navigation <-
        match requestedNavigation with
        | LookNavigation -> MouseLook
        | PivotNavigation ->
            match state.active_mouse_navigation with
            | MousePivot _ -> state.active_mouse_navigation
            | MouseLook
            | MousePan _ -> MousePivot(navigation_target state ViewNavigationMode.Pivot state.gumball_pivot_target)
        | PanNavigation ->
            match state.active_mouse_navigation with
            | MousePan _ -> state.active_mouse_navigation
            | MouseLook
            | MousePivot _ ->
                let panTarget = navigation_target state ViewNavigationMode.Pan None
                MousePan(panTarget, pan_units_per_radian panTarget state.camera)

    if previousNavigation <> requestedNavigation then
        InputAccumulator.drain_mouse input |> ignore
        state.wheel_remainder <- 0L

    retargetChange

let apply_navigation_wheel (steps: int64) (state: FlyState) =
    if steps = 0L then
        ViewChange.none
    else
        match state.active_mouse_navigation with
        | MouseLook -> ViewChange.none
        | MousePivot target
        | MousePan(target, _) ->
            let zoomScale = ViewSettings.ZoomScale

            if not (RhinoMath.IsValidDouble zoomScale) || zoomScale <= RhinoMath.ZeroTolerance then
                ViewChange.none
            else
                let magnification = System.Math.Pow(1. / zoomScale, float steps)

                if
                    not (RhinoMath.IsValidDouble magnification)
                    || magnification <= RhinoMath.ZeroTolerance
                then
                    ViewChange.none
                else
                    let previousCamera = state.camera

                    let parallelFlight = state.projection = ViewProjectionKind.Parallel

                    state.camera <- Movement.dolly_towards target magnification state.camera

                    if not (CameraState.valid state.camera) then
                        state.restore_camera_on_exit <- true
                        failwith "Mouse-wheel input produced an invalid camera state."

                    if state.camera <> previousCamera then
                        match state.active_mouse_navigation with
                        | MousePan(panTarget, _) ->
                            state.active_mouse_navigation <-
                                MousePan(panTarget, pan_units_per_radian panTarget state.camera)
                        | MouseLook
                        | MousePivot _ -> ()

                    { camera_changed = state.camera <> previousCamera
                      parallel_magnification = if parallelFlight then magnification else 1. }

let apply_mouse_input (input: InputAccumulator.State) (state: FlyState) =
    let struct (dx, dy) = InputAccumulator.drain_mouse input

    if dx = 0L && dy = 0L then
        ViewChange.none
    else
        let previous = state.camera
        let parallelView = state.config.movement.parallel_view

        let parallelFlight = state.projection = ViewProjectionKind.Parallel

        let mouseSensitivity =
            if parallelFlight then
                parallelView.mouse_sensitivity
            else
                state.config.mouse.sensitivity

        let pivotMultiplier =
            if parallelFlight then
                parallelView.mouse_pivot_multiplier
            else
                state.config.mouse.pivot_multiplier

        let panMultiplier =
            if parallelFlight then
                parallelView.mouse_pan_multiplier
            else
                state.config.mouse.pan_multiplier

        match state.active_mouse_navigation with
        | MouseLook -> state.camera <- Movement.mouse_look state.config.mouse mouseSensitivity dx dy state.camera
        | MousePivot target ->
            state.camera <-
                Movement.mouse_pivot state.config.mouse mouseSensitivity pivotMultiplier target dx dy state.camera
        | MousePan(panTarget, unitsPerRadian) ->
            state.camera <-
                Movement.mouse_pan state.config.mouse mouseSensitivity panMultiplier unitsPerRadian dx dy state.camera

            let translation = state.camera.position - previous.position

            if not translation.IsZero then
                state.active_mouse_navigation <- MousePan(panTarget + translation, unitsPerRadian)

        if CameraState.valid state.camera then
            ViewChange.camera previous state.camera
        else
            state.restore_camera_on_exit <- true
            failwith "Mouse input produced an invalid camera state."

let parallel_magnification_factor (state: FlyState) (forwardDistance: float) =
    let viewport = state.viewport
    let parallelView = state.config.movement.parallel_view

    if
        state.projection <> ViewProjectionKind.Parallel
        || abs forwardDistance <= RhinoMath.ZeroTolerance
    then
        1.
    else
        let mutable left = 0.
        let mutable right = 0.
        let mutable bottom = 0.
        let mutable top = 0.
        let mutable nearDistance = 0.
        let mutable farDistance = 0.

        if viewport.GetFrustum(&left, &right, &bottom, &top, &nearDistance, &farDistance) then
            let width = right - left

            if RhinoMath.IsValidDouble width && width > RhinoMath.ZeroTolerance then
                let requestedExponent = forwardDistance * parallelView.zoom_speed_multiplier / width
                let exponent = Movement.clamp -0.25 0.25 requestedExponent
                let factor = Math.Exp exponent

                if RhinoMath.IsValidDouble factor && factor > RhinoMath.ZeroTolerance then
                    factor
                else
                    1.
            else
                1.
        else
            1.

let apply (state: FlyState) (change: ViewChange) =
    if change.camera_changed then
        state.viewport.SetCameraLocations(state.camera.target, state.camera.position)
        state.viewport.CameraUp <- state.camera.up

    let projectionRequested = change.parallel_magnification <> 1.

    let projectionChanged =
        if not projectionRequested then
            false
        elif
            RhinoMath.IsValidDouble change.parallel_magnification
            && change.parallel_magnification > RhinoMath.ZeroTolerance
            && state.viewport.Magnify(change.parallel_magnification, true)
        then
            true
        else
            state.restore_camera_on_exit <- true
            failwith "Rhino could not magnify the parallel viewport."

    if change.camera_changed || projectionChanged then
        redraw state

let entry_perspective_lens_changes (state: FlyState) =
    let lens = state.config.behavior.perspective_lens

    state.projection <> ViewProjectionKind.Parallel
    && (Option.isSome lens.forced_on_flight_start_mm
        || lens.delta_during_flight_mm <> 0.)

let apply_entry_perspective_lens (state: FlyState) =
    let lensConfig = state.config.behavior.perspective_lens

    if entry_perspective_lens_changes state then
        let forcedOrOriginal =
            match lensConfig.forced_on_flight_start_mm, state.original_camera.perspective_lens_length_mm with
            | Some forcedLength, _ -> forcedLength
            | None, ValueSome originalLength -> originalLength
            | None, ValueNone -> failwith "The perspective viewport has no lens length."

        let lens = forcedOrOriginal + lensConfig.delta_during_flight_mm

        if not (RhinoMath.IsValidDouble lens) || lens <= 0. then
            failwith $"The configured lens adjustment produces an invalid lens length: {lens} mm"

        state.viewport.Camera35mmLensLength <- lens
        state.perspective_lens_length_mm <- lens
