module RhinosCanFly.FlightCamera

open Rhino
open Rhino.Display
open Rhino.Geometry

let fallback_navigation_target (view: RhinoView) =
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
        && nearDistance > RhinoMath.ZeroTolerance
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

let navigation_target (state: FlyState) (gumballTarget: Point3d option) =
    let viewport = state.viewport

    match gumballTarget with
    | Some target when ViewTarget.target_is_in_front viewport target -> target
    | Some _
    | None ->
        match ViewTarget.selected_target state.config.behavior.view_target state.speed viewport with
        | Some target when ViewTarget.target_is_in_front viewport target -> target
        | Some _
        | None -> fallback_navigation_target state.view

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
            | MousePan _ -> MousePivot(navigation_target state state.gumball_pivot_target)
        | PanNavigation ->
            match state.active_mouse_navigation with
            | MousePan _ -> state.active_mouse_navigation
            | MouseLook
            | MousePivot _ ->
                let panTarget = navigation_target state None
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
        let previous = state.camera

        let change =
            match state.active_mouse_navigation with
            | MouseLook ->
                state.camera <- Movement.look state.config.mouse dx dy state.camera

                if state.camera.yaw <> previous.yaw || state.camera.pitch <> previous.pitch then
                    DirectionChanged
                else
                    NoCameraChange
            | MousePivot target ->
                state.camera <- Movement.mouse_pivot state.config.mouse target dx dy state.camera

                let positionChanged = state.camera.position <> previous.position

                let directionChanged =
                    state.camera.yaw <> previous.yaw || state.camera.pitch <> previous.pitch

                if positionChanged then
                    if directionChanged then
                        PositionAndDirectionChanged
                    else
                        PositionChanged
                elif directionChanged then
                    DirectionChanged
                else
                    NoCameraChange
            | MousePan unitsPerRadian ->
                state.camera <- Movement.mouse_pan state.config.mouse unitsPerRadian dx dy state.camera

                if state.camera.position <> previous.position then
                    PositionChanged
                else
                    NoCameraChange

        if CameraState.valid state.camera then
            change
        else
            state.restore_camera_on_exit <- true
            failwith "Mouse input produced an invalid camera state."

let apply (state: FlyState) (change: CameraChange) =
    match change with
    | NoCameraChange -> ()
    | PositionChanged
    | DirectionChanged
    | PositionAndDirectionChanged ->
        let invalidExit =
            if not (PlatformInput.viewport_host_is_active state.host_identity state.view) then
                Some HostInvalid
            elif PlatformInput.foreground_root_window () <> state.host_identity.root_window then
                Some FocusLost
            else
                None

        match invalidExit with
        | Some reason ->
            state.restore_camera_on_exit <- true
            FlyState.request_exit reason state
            failwith "The active Rhino document or viewport changed during flight."
        | None -> ()

        state.viewport.SetCameraLocations(state.camera.target, state.camera.position)
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
