module RhinosCanFly.FlightCamera

open System
open Rhino
open Rhino.ApplicationSettings
open Rhino.Display
open Rhino.Geometry

let navigation_target (state: FlyState) (mode: ViewNavigationMode) =
    let selection =
        ViewTarget.resolve_navigation_target
            state.config.behavior
            state.hidden_gumball_plane
            mode
            state.view
            state.viewport
            (ViewTarget.viewport_center state.viewport)

    selection.target

let pan_units_per_radian (target: Point3d) (camera: CameraState) =
    let depth = Movement.target_depth target camera

    if RhinoMath.IsValidDouble depth && depth > RhinoMath.ZeroTolerance then
        MousePanUnitsPerRadian depth
    else
        MousePanUnitsPerRadian 1.

let pivot_angle_scales (state: FlyState) =
    let parallel_projection = state.config.movement.parallel_projection

    let struct (sensitivity, pivot_multiplier) =
        if state.projection = ViewProjectionKind.Parallel then
            struct (parallel_projection.mouse_sensitivity, parallel_projection.mouse_pivot_multiplier)
        else
            struct (state.config.mouse.sensitivity, state.config.mouse.pivot_multiplier)

    let (MousePivotMultiplier multiplier) = pivot_multiplier

    Movement.mouse_angle_scales state.config.mouse.x_mode state.config.mouse.y_mode sensitivity multiplier

let create_pivot_drag (center: Point3d) (state: FlyState) =
    let struct (horizontal_scale, vertical_scale) = pivot_angle_scales state
    PivotOrbit.create center state.camera horizontal_scale vertical_scale

let reset_pivot_drag (center: Point3d) (drag: PivotDragState) (state: FlyState) =
    let struct (horizontal_scale, vertical_scale) = pivot_angle_scales state
    PivotOrbit.reset center state.camera horizontal_scale vertical_scale drag

let rebase_active_pivot (state: FlyState) =
    match state.active_mouse_navigation with
    | MousePivot drag -> PivotOrbit.rebase state.camera drag
    | MouseLook
    | MousePan _ -> ()

let untilt (state: FlyState) =
    let previous = state.camera
    let struct (_, up) = Movement.camera_basis previous.direction Vector3d.ZAxis

    state.camera <- { previous with up = up }
    ViewChange.camera previous state.camera

let sync_camera_from_viewport (accepted_target: Point3d voption) (state: FlyState) =
    if FlyState.can_write_camera state then
        try
            let camera = ViewportNavigation.capture_camera state.viewport

            if FlyState.can_write_camera state then
                state.camera <- camera

                match accepted_target with
                | ValueSome target -> state.key_pivot_target <- target
                | ValueNone -> ()

                match state.active_mouse_navigation with
                | MousePivot drag -> reset_pivot_drag (ValueOption.defaultValue drag.center accepted_target) drag state
                | MousePan(pan_target, _) ->
                    let target = ValueOption.defaultValue pan_target accepted_target
                    state.active_mouse_navigation <- MousePan(target, pan_units_per_radian target camera)
                | MouseLook -> ()
        with _ ->
            state.restore_camera_on_exit <- true
            reraise ()

let redraw (state: FlyState) =
    if FlyState.can_write_camera state then
        state.view.Redraw()

    if
        FlyState.can_write_camera state
        && state.config.behavior.viewport_paint_mode = ViewportPaintMode.Immediate
    then
        PlatformInput.update_window state.view

let toggle_projection (state: FlyState) =
    let viewport = state.viewport

    let conversion =
        if not (FlyState.can_write_camera state) then
            struct (state.projection, false)
        elif state.projection = ViewProjectionKind.Parallel then
            let (PerspectiveLensLengthMm lens) = state.perspective_lens_length
            let target_distance = Movement.target_distance state.camera

            match state.perspective_projection with
            | ViewProjectionKind.TwoPointPerspective ->
                struct (ViewProjectionKind.TwoPointPerspective,
                        viewport.ChangeToTwoPointPerspectiveProjection(target_distance, viewport.CameraY, lens))
            | ViewProjectionKind.Parallel
            | ViewProjectionKind.Perspective ->
                struct (ViewProjectionKind.Perspective,
                        viewport.ChangeToPerspectiveProjection(target_distance, false, lens))
        else
            state.perspective_projection <- state.projection
            struct (ViewProjectionKind.Parallel, viewport.ChangeToParallelProjection false)

    let struct (next_projection, changed) = conversion

    if FlyState.can_write_camera state && not changed then
        state.restore_camera_on_exit <- true
        failwith "Rhino could not change the viewport projection."

    if FlyState.can_write_camera state then
        state.projection <- next_projection
        sync_camera_from_viewport ValueNone state
        state.wheel_remainder <- 0L
        redraw state

let apply_retarget_request (scope: RetargetScope) (mode: RetargetMode) (state: FlyState) =
    if mode <> RetargetMode.Off then
        let outcome =
            NavigationTarget.acquire_and_apply
                (fun () -> FlyState.can_write_camera state)
                state.config.behavior.retarget
                scope
                mode
                state.view
                (ViewTarget.viewport_center state.viewport)

        if
            scope = RetargetScope.AllViews
            && (ValueOption.isSome outcome.source_target || not outcome.errors.IsEmpty)
            && PlatformInput.viewport_host_is_active state.host_identity state.view
        then
            sync_camera_from_viewport outcome.source_target state

        if not outcome.errors.IsEmpty then
            let errors = String.concat "; " outcome.errors
            RhinoApp.WriteLine $"Retarget: {errors}"

    ViewChange.none

let update_navigation_mode (state: FlyState) =
    let requested_navigation =
        if state.keyboard_pan_held || state.mouse_pan_hold_buttons <> 0 then
            PanNavigation
        elif state.keyboard_pivot_held || state.mouse_pivot_hold_buttons <> 0 then
            PivotNavigation
        else
            state.latched_mouse_navigation

    let previous_navigation =
        match state.active_mouse_navigation with
        | MouseLook -> LookNavigation
        | MousePivot _ -> PivotNavigation
        | MousePan _ -> PanNavigation

    let next_navigation =
        match requested_navigation with
        | LookNavigation -> MouseLook
        | PivotNavigation ->
            match state.active_mouse_navigation with
            | MousePivot _ -> state.active_mouse_navigation
            | MouseLook
            | MousePan _ ->
                let center = navigation_target state ViewNavigationMode.Pivot

                if FlyState.can_write_camera state then
                    MousePivot(create_pivot_drag center state)
                else
                    state.active_mouse_navigation
        | PanNavigation ->
            match state.active_mouse_navigation with
            | MousePan _ -> state.active_mouse_navigation
            | MouseLook
            | MousePivot _ ->
                let pan_target = navigation_target state ViewNavigationMode.Pan

                if FlyState.can_write_camera state then
                    MousePan(pan_target, pan_units_per_radian pan_target state.camera)
                else
                    state.active_mouse_navigation

    let changed =
        FlyState.can_write_camera state && previous_navigation <> requested_navigation

    if FlyState.can_write_camera state then
        state.active_mouse_navigation <- next_navigation

    if changed then
        state.wheel_remainder <- 0L

    changed

let apply_navigation_wheel (steps: float) (state: FlyState) =
    if steps = 0. then
        ViewChange.none
    else
        let target =
            match state.active_mouse_navigation with
            | MouseLook -> state.camera.target
            | MousePivot drag -> drag.center
            | MousePan(target, _) -> target

        let zoom_scale = ViewSettings.ZoomScale

        if
            not (RhinoMath.IsValidDouble zoom_scale)
            || zoom_scale <= RhinoMath.ZeroTolerance
        then
            ViewChange.none
        else
            let magnification = System.Math.Pow(1. / zoom_scale, steps)

            if
                not (RhinoMath.IsValidDouble magnification)
                || magnification <= RhinoMath.ZeroTolerance
            then
                ViewChange.none
            else
                let previous_camera = state.camera

                let parallel_flight = state.projection = ViewProjectionKind.Parallel

                state.camera <- Movement.dolly_towards target magnification state.camera

                if not (CameraState.valid state.camera) then
                    state.restore_camera_on_exit <- true
                    failwith "Mouse-wheel input produced an invalid camera state."

                if state.camera <> previous_camera then
                    match state.active_mouse_navigation with
                    | MousePivot drag -> PivotOrbit.rebase state.camera drag
                    | MousePan(pan_target, _) ->
                        state.active_mouse_navigation <-
                            MousePan(pan_target, pan_units_per_radian pan_target state.camera)
                    | MouseLook -> ()

                { camera_changed = state.camera <> previous_camera
                  parallel_magnification = if parallel_flight then magnification else 1. }

let apply_mouse_delta (dx: int64) (dy: int64) (state: FlyState) =
    if dx = 0L && dy = 0L then
        ViewChange.none
    else
        let previous = state.camera
        let parallel_projection = state.config.movement.parallel_projection

        let parallel_flight = state.projection = ViewProjectionKind.Parallel

        let mouse_sensitivity =
            if parallel_flight then
                parallel_projection.mouse_sensitivity
            else
                state.config.mouse.sensitivity

        let pan_multiplier =
            if parallel_flight then
                parallel_projection.mouse_pan_multiplier
            else
                state.config.mouse.pan_multiplier

        match state.active_mouse_navigation with
        | MouseLook -> state.camera <- Movement.mouse_look state.config.mouse mouse_sensitivity dx dy state.camera
        | MousePivot drag -> state.camera <- PivotOrbit.apply_delta dx dy drag
        | MousePan(pan_target, units_per_radian) ->
            state.camera <-
                Movement.mouse_pan
                    state.config.mouse
                    mouse_sensitivity
                    pan_multiplier
                    units_per_radian
                    dx
                    dy
                    state.camera

            let translation = state.camera.position - previous.position

            if not translation.IsZero then
                state.active_mouse_navigation <- MousePan(pan_target + translation, units_per_radian)

        if CameraState.valid state.camera then
            ViewChange.camera previous state.camera
        else
            state.restore_camera_on_exit <- true
            failwith "Mouse input produced an invalid camera state."

let parallel_zoom_factor (width: float) (forward_distance: float) (multiplier: float) (seconds: float) =
    if
        not (RhinoMath.IsValidDouble width)
        || width <= RhinoMath.ZeroTolerance
        || not (RhinoMath.IsValidDouble forward_distance)
        || not (RhinoMath.IsValidDouble multiplier)
        || multiplier < 0.
        || not (RhinoMath.IsValidDouble seconds)
        || seconds <= 0.
    then
        1.
    else
        // Bound zoom per second, including the transition into and out of the limit.
        let maximum_rate = 15.
        let rate = forward_distance / seconds / width * multiplier

        if rate = 0. then
            1.
        elif rate >= maximum_rate then
            Math.Exp(maximum_rate * seconds)
        elif rate > 0. then
            let until_limit = (1. - rate / maximum_rate) / rate

            if seconds <= until_limit then
                1. / (1. - rate * seconds)
            else
                maximum_rate / rate * Math.Exp(maximum_rate * (seconds - until_limit))
        elif -rate <= maximum_rate then
            1. / (1. - rate * seconds)
        else
            let until_limit = Math.Log(-rate / maximum_rate) / maximum_rate

            if seconds <= until_limit then
                Math.Exp(-maximum_rate * seconds)
            else
                maximum_rate / -rate / (1. + maximum_rate * (seconds - until_limit))

let parallel_magnification_factor (state: FlyState) (forward_distance: float) (seconds: float) =
    let viewport = state.viewport
    let parallel_projection = state.config.movement.parallel_projection

    if
        state.projection <> ViewProjectionKind.Parallel
        || abs forward_distance <= RhinoMath.ZeroTolerance
    then
        1.
    else
        let mutable left = 0.
        let mutable right = 0.
        let mutable bottom = 0.
        let mutable top = 0.
        let mutable near_distance = 0.
        let mutable far_distance = 0.

        if viewport.GetFrustum(&left, &right, &bottom, &top, &near_distance, &far_distance) then
            let width = right - left

            if RhinoMath.IsValidDouble width && width > RhinoMath.ZeroTolerance then
                let factor =
                    parallel_zoom_factor width forward_distance parallel_projection.zoom_speed_multiplier seconds

                if RhinoMath.IsValidDouble factor && factor > RhinoMath.ZeroTolerance then
                    factor
                else
                    1.
            else
                1.
        else
            1.

let apply_movement (movement: FlightMovementInput) (seconds: float) (state: FlyState) =
    if
        seconds <= 0.
        || not (FlightInput.movement_active movement)
        || not (FlyState.can_write_camera state)
    then
        ViewChange.none
    else
        let previous = state.camera

        let vertical_speed_multiplier =
            if state.projection = ViewProjectionKind.Parallel then
                state.config.movement.parallel_projection.up_down_multiplier
            else
                state.config.movement.vertical_speed_multiplier

        let step =
            Movement.step
                state.config.movement
                vertical_speed_multiplier
                state.walking_plane
                movement
                state.key_pivot_target
                seconds
                previous

        if not (CameraState.valid step.camera) then
            state.restore_camera_on_exit <- true
            failwith "Movement produced an invalid camera state."

        let magnification =
            parallel_magnification_factor state step.forward_distance seconds

        match state.active_mouse_navigation with
        | MousePivot drag ->
            PivotOrbit.transform_for_flight_movement step.translation state.key_pivot_target step.key_pivot_angle drag
        | MousePan(target, units_per_radian) ->
            state.active_mouse_navigation <-
                MousePan(
                    Movement.orbit_point state.key_pivot_target step.key_pivot_angle (target + step.translation),
                    units_per_radian
                )
        | MouseLook -> ()

        state.camera <- step.camera

        { camera_changed = step.camera <> previous
          parallel_magnification = magnification }

// Update the camera now; redraw once after the input batch.
let write_view (state: FlyState) (change: ViewChange) =
    let camera_changed = change.camera_changed && FlyState.can_write_camera state

    if camera_changed then
        state.viewport.SetCameraLocations(state.camera.target, state.camera.position)

        if FlyState.can_write_camera state then
            state.viewport.CameraUp <- state.camera.up

    let projection_requested = change.parallel_magnification <> 1.

    let projection_changed =
        if not projection_requested || not (FlyState.can_write_camera state) then
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

    camera_changed || projection_changed

let entry_perspective_lens_changes (state: FlyState) =
    let lens = state.config.behavior.perspective_lens
    let (PerspectiveLensDeltaMm delta) = lens.delta_during_flight

    state.projection <> ViewProjectionKind.Parallel
    && (Option.isSome lens.forced_on_flight_start || delta <> 0.)

let apply_entry_perspective_lens (state: FlyState) =
    let lens_config = state.config.behavior.perspective_lens

    if entry_perspective_lens_changes state then
        let forced_or_original =
            match lens_config.forced_on_flight_start with
            | Some forced_length -> forced_length
            | None ->
                match state.original_camera.perspective_lens_length with
                | ValueSome original_length -> original_length
                | ValueNone -> failwith "The perspective viewport has no lens length."

        let (PerspectiveLensLengthMm absolute_lens) = forced_or_original
        let (PerspectiveLensDeltaMm lens_delta) = lens_config.delta_during_flight
        let lens = absolute_lens + lens_delta

        if not (RhinoMath.IsValidDouble lens) || lens <= 0. then
            failwith $"The configured lens adjustment produces an invalid lens length: {lens} mm"

        if FlyState.can_write_camera state then
            state.viewport.Camera35mmLensLength <- lens
            state.perspective_lens_length <- PerspectiveLensLengthMm lens
