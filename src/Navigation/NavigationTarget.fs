module RhinosCanFly.NavigationTarget

// Rhino 9 deprecates GetViewList(bool, bool), but Rhino 7 has no replacement.
#nowarn "44"

open Rhino
open Rhino.ApplicationSettings
open Rhino.Display
open Rhino.Geometry

let apply
    (loaded: ConfigLoadResult)
    (target_point: NavigationTargetPoint)
    (mode: ViewNavigationMode)
    (view: RhinoView)
    (can_apply: unit -> bool)
    =
    try
        if isNull view || isNull view.Document then
            Error "The navigation viewport is unavailable."
        else
            ViewTarget.apply_for_navigation loaded.config.behavior mode view view.ActiveViewport target_point can_apply
            |> Ok
    with error ->
        Error $"Could not set the navigation target: {error.Message}"

let prepare
    (loaded: ConfigLoadResult)
    (host: ViewportHostIdentity)
    (target_point: NavigationTargetPoint)
    (mode: ViewNavigationMode)
    (can_apply: unit -> bool)
    =
    try
        let view = RhinoView.FromRuntimeSerialNumber host.view_serial_number

        if isNull view || isNull view.Document then
            Error "The navigation viewport is unavailable."
        elif PlatformInput.foreground_root_window () <> host.root_window then
            Error "The navigation viewport is no longer active."
        else
            let document = view.Document
            let active_document = RhinoDoc.ActiveDoc

            if
                isNull active_document
                || active_document.RuntimeSerialNumber <> host.document_serial_number
                || document.RuntimeSerialNumber <> host.document_serial_number
            then
                Error "The navigation document is no longer active."
            else
                let active_view = document.Views.ActiveView

                if isNull active_view || active_view.RuntimeSerialNumber <> host.view_serial_number then
                    Error "The navigation viewport is not active yet."
                else
                    let current_host = PlatformInput.capture_viewport_host view

                    if current_host = host && can_apply () then
                        let permitted () =
                            can_apply () && PlatformInput.viewport_host_is_foreground host view

                        match apply loaded target_point mode view permitted with
                        | Ok target -> Ok(struct (current_host, target))
                        | Error error -> Error error
                    else
                        Error "The navigation viewport changed before it could be prepared."
    with error ->
        Error $"Could not prepare view navigation: {error.Message}"

let apply_camera_operation
    (target: Point3d)
    (can_apply: unit -> bool)
    (prepare: unit -> bool)
    (set_location: unit -> unit)
    (set_up: unit -> unit)
    (redraw: unit -> unit)
    =
    let mutable accepted = ValueNone

    try
        if not (can_apply ()) then
            invalidOp "Retargeting was cancelled."

        if not (prepare ()) then
            invalidOp "Rhino declined the camera operation."

        if not (can_apply ()) then
            invalidOp "Retargeting was cancelled."

        set_location ()

        if not (can_apply ()) then
            invalidOp "Retargeting was cancelled."

        set_up ()
        accepted <- ValueSome target

        if can_apply () then
            redraw ()

        { source_target = accepted
          errors = [] }
    with error ->
        { source_target = accepted
          errors = [ error.Message ] }

let distance_camera_position (target: Point3d) (unit_direction: Vector3d) (distance: float) =
    let position = target - unit_direction * distance
    let depth = Vector3d.Multiply(target - position, unit_direction)

    if
        not target.IsValid
        || not position.IsValid
        || not unit_direction.IsValid
        || abs (unit_direction.SquareLength - 1.) > 1e-6
        || not (RhinoMath.IsValidDouble distance)
        || distance <= RhinoMath.ZeroTolerance
        || not (RhinoMath.IsValidDouble depth)
        || depth <= RhinoMath.ZeroTolerance
    then
        invalidOp "The retarget camera is outside the usable coordinate range."

    position

let move_view_to_target
    (can_apply: unit -> bool)
    (distances: ViewTarget.FallbackDistances)
    (target: Point3d)
    (view: RhinoView)
    =
    if not (isNull view) then
        let viewport = view.ActiveViewport
        let viewport_id = viewport.Id
        let mutable direction = viewport.CameraDirection
        let up = viewport.CameraY

        let distance =
            ViewTarget.projection_distance distances viewport.IsParallelProjection

        if direction.Unitize() then
            apply_camera_operation
                target
                (fun () -> can_apply () && view.ActiveViewportID = viewport_id)
                (fun () -> true)
                (fun () -> viewport.SetCameraLocations(target, distance_camera_position target direction distance))
                (fun () -> viewport.CameraUp <- up)
                (fun () -> view.Redraw())
        else
            { source_target = ValueNone
              errors = [ "The retarget distance or camera direction is invalid." ] }
    else
        { source_target = ValueNone
          errors = [ "The viewport is unavailable." ] }

let zoom_view_to_selection (can_apply: unit -> bool) (target: Point3d) (bounds: BoundingBox) (view: RhinoView) =
    if not (isNull view) then
        let viewport = view.ActiveViewport
        let viewport_id = viewport.Id
        let up = viewport.CameraY

        apply_camera_operation
            target
            (fun () -> can_apply () && view.ActiveViewportID = viewport_id)
            (fun () -> viewport.ZoomBoundingBox bounds)
            (fun () ->
                let offset = target - viewport.CameraTarget
                viewport.SetCameraLocations(target, viewport.CameraLocation + offset))
            (fun () -> viewport.CameraUp <- up)
            (fun () -> view.Redraw())
    else
        { source_target = ValueNone
          errors = [ "The viewport is unavailable." ] }

let apply_to_views_with
    (scope: RetargetScope)
    (source: 'View)
    (others: 'View seq)
    (apply: 'View -> ApplicationOutcome)
    =
    let errors = ResizeArray<string>()

    let apply_one (view: 'View) =
        try
            let outcome = apply view
            errors.AddRange outcome.errors
            outcome.source_target
        with error ->
            errors.Add error.Message
            ValueNone

    let source_target =
        if scope = RetargetScope.AllViews then
            apply_one source
        else
            ValueNone

    try
        for other in others do
            apply_one other |> ignore
    with error ->
        errors.Add error.Message

    { source_target = source_target
      errors = List.ofSeq errors }

let apply_to_views (scope: RetargetScope) (view: RhinoView) (apply: RhinoView -> ApplicationOutcome) =
    let others =
        view.Document.Views.GetViewList(true, false)
        |> Seq.filter (fun (other: RhinoView) ->
            not (isNull other) && other.RuntimeSerialNumber <> view.RuntimeSerialNumber)

    apply_to_views_with scope view others (fun (target_view: RhinoView) ->
        let name = target_view.ActiveViewport.Name

        try
            let outcome = apply target_view

            { outcome with
                errors = outcome.errors |> List.map (fun (error: string) -> $"{name}: {error}") }
        with error ->
            { source_target = ValueNone
              errors = [ $"{name}: {error.Message}" ] })

let zoom_views_to_selection
    (can_apply: unit -> bool)
    (retarget: RetargetConfig)
    (scope: RetargetScope)
    (target: Point3d)
    (bounds: BoundingBox)
    (view: RhinoView)
    =
    let previous_perspective_border = ViewSettings.ZoomExtentsPerspectiveViewBorder
    let previous_parallel_border = ViewSettings.ZoomExtentsParallelViewBorder

    let mutable outcome =
        { source_target = ValueNone
          errors = [] }

    try
        try
            ViewSettings.ZoomExtentsPerspectiveViewBorder <- retarget.perspective_zoom_border
            ViewSettings.ZoomExtentsParallelViewBorder <- retarget.parallel_zoom_border
            outcome <- apply_to_views scope view (zoom_view_to_selection can_apply target bounds)
        with error ->
            outcome <-
                { outcome with
                    errors = outcome.errors @ [ error.Message ] }
    finally
        try
            ViewSettings.ZoomExtentsPerspectiveViewBorder <- previous_perspective_border
        with error ->
            outcome <-
                { outcome with
                    errors = outcome.errors @ [ error.Message ] }

        try
            ViewSettings.ZoomExtentsParallelViewBorder <- previous_parallel_border
        with error ->
            outcome <-
                { outcome with
                    errors = outcome.errors @ [ error.Message ] }

    outcome

let set_view_target (can_apply: unit -> bool) (target: Point3d) (view: RhinoView) =
    if not (isNull view) then
        let viewport = view.ActiveViewport
        let viewport_id = viewport.Id

        apply_camera_operation
            target
            (fun () -> can_apply () && view.ActiveViewportID = viewport_id)
            (fun () -> true)
            (fun () -> viewport.SetCameraTarget(target, false))
            (fun () -> ())
            (fun () -> view.Redraw())
    else
        { source_target = ValueNone
          errors = [ "The viewport is unavailable." ] }

let apply_selection
    (can_apply: unit -> bool)
    (retarget: RetargetConfig)
    (scope: RetargetScope)
    (selection: ViewTarget.RetargetSelection)
    (view: RhinoView)
    =
    if not (can_apply ()) then
        { source_target = ValueNone
          errors = [ "Retargeting was cancelled." ] }
    else
        match selection.bounds, selection.fallback_distances with
        | ValueSome bounds, _ -> zoom_views_to_selection can_apply retarget scope selection.target bounds view
        | ValueNone, ValueSome distances ->
            apply_to_views scope view (move_view_to_target can_apply distances selection.target)
        | ValueNone, ValueNone -> apply_to_views scope view (set_view_target can_apply selection.target)

let acquire_and_apply
    (can_apply: unit -> bool)
    (retarget: RetargetConfig)
    (scope: RetargetScope)
    (mode: RetargetMode)
    (view: RhinoView)
    (point: ViewportClientPoint)
    =
    match ViewTarget.selected_selection_at retarget mode view view.ActiveViewport point with
    | None ->
        { source_target = ValueNone
          errors = [] }
    | Some selection -> apply_selection can_apply retarget scope selection view

let retarget
    (loaded: ConfigLoadResult)
    (host: ViewportHostIdentity)
    (client_point: ViewportClientPoint)
    (mode: RetargetMode)
    (can_apply: unit -> bool)
    =
    try
        let view = RhinoView.FromRuntimeSerialNumber host.view_serial_number

        if
            not (PlatformInput.viewport_host_is_active host view)
            || PlatformInput.foreground_root_window () <> host.root_window
        then
            invalidOp "The retarget viewport is no longer active."

        let behavior = loaded.config.behavior

        let permitted () =
            can_apply () && PlatformInput.viewport_host_is_foreground host view

        acquire_and_apply permitted behavior.retarget RetargetScope.AllViews mode view client_point
    with error ->
        { source_target = ValueNone
          errors = [ $"Could not retarget the viewports: {error.Message}" ] }
