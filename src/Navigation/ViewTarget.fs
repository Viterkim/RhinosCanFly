module RhinosCanFly.ViewTarget

open System
open System.Diagnostics
open Rhino
open Rhino.ApplicationSettings
open Rhino.DocObjects
open Rhino.Display
open Rhino.Geometry
open Rhino.Input.Custom

[<Struct>]
type FallbackDistances = { perspective: float; parallel: float }

[<Struct>]
type RetargetSelection =
    { target: Point3d
      bounds: BoundingBox voption
      fallback_distances: FallbackDistances voption }

[<Struct>]
type SelectedObjectPickMode =
    | BoundsWhenNothingElsePicked
    | ExactUnderCursor

[<Struct; RequireQualifiedAccess>]
type NavigationTargetSource =
    | SelectionCenter
    | Gumball
    | ObjectCenter
    | NativeHit
    | SelectedBounds
    | VisibleSurface
    | Distance
    | ExistingCameraTarget

[<Struct>]
type NavigationSelection =
    { target: Point3d
      source: NavigationTargetSource }

[<Struct>]
type ObjectTarget =
    { navigation: NavigationSelection option
      bounds: BoundingBox voption
      native_hit: Point3d }

[<Struct>]
type ObjectCandidate =
    { rhino_object: RhinoObject
      hit: Point3d
      depth: float }

let gumball_plane (view: RhinoView) =
    if isNull view || isNull view.Document then
        ValueNone
    else
        let mutable plane = Plane.Unset

        if view.Document.GetGumballPlane(&plane) && plane.IsValid && plane.Origin.IsValid then
            ValueSome plane
        else
            ValueNone

let target_is_in_front (viewport: RhinoViewport) (target: Point3d) =
    let mutable direction = viewport.CameraDirection
    let offset = target - viewport.CameraLocation

    target.IsValid
    && direction.Unitize()
    && Vector3d.Multiply(offset, direction) > RhinoMath.ZeroTolerance

let viewport_center (viewport: RhinoViewport) =
    let bounds = viewport.Bounds

    { x = bounds.Width / 2
      y = bounds.Height / 2 }

let query_geometry_target_at (viewport: RhinoViewport) (point: ViewportClientPoint) =
    try
        use capture = new ZBufferCapture(viewport)
        let hits = capture.HitCount()

        if hits <= 0 then
            Ok None
        else
            let depth = capture.ZValueAt(point.x, point.y)

            if Single.IsNaN depth || Single.IsInfinity depth || depth <= 0.f || depth >= 1.f then
                Ok None
            else
                let target = capture.WorldPointAt(point.x, point.y)

                if target_is_in_front viewport target then
                    Ok(Some target)
                else
                    Ok None
    with error ->
        Error $"RhinosCanFly geometry target: {error}"

let target_or_miss (result: Result<'Target option, string>) =
    match result with
    | Ok target -> target
    | Error error ->
        Debug.WriteLine error
        None

let try_geometry_target_at (viewport: RhinoViewport) (point: ViewportClientPoint) =
    query_geometry_target_at viewport point |> target_or_miss

let object_bounds (accurate: bool) (rhino_object: RhinoObject) =
    try
        if isNull rhino_object then
            ValueNone
        else
            let geometry = rhino_object.Geometry

            if isNull geometry then
                ValueNone
            else
                let bounds = geometry.GetBoundingBox accurate
                if bounds.IsValid then ValueSome bounds else ValueNone
    with error ->
        Debug.WriteLine $"RhinosCanFly object bounds: {error}"
        ValueNone

let selection_center_selection (view: RhinoView) =
    if isNull view || isNull view.Document then
        None
    else
        try
            let settings = ObjectEnumeratorSettings()
            settings.SelectedObjectsFilter <- true
            settings.SubObjectSelected <- true

            let mutable selection_bounds = ValueNone

            let include_bounds (bounds: BoundingBox) =
                if bounds.IsValid then
                    selection_bounds <-
                        match selection_bounds with
                        | ValueSome current -> ValueSome(BoundingBox.Union(current, bounds))
                        | ValueNone -> ValueSome bounds

            for rhino_object in view.Document.Objects.GetObjectList settings do
                if not (isNull rhino_object) then
                    let selected_sub_objects = rhino_object.GetSelectedSubObjects()

                    if isNull selected_sub_objects || selected_sub_objects.Length = 0 then
                        match object_bounds true rhino_object with
                        | ValueSome bounds -> include_bounds bounds
                        | ValueNone -> ()
                    else
                        for component_index in selected_sub_objects do
                            use reference = new ObjRef(view.Document, rhino_object.Id, component_index)
                            let geometry = reference.Geometry()

                            if not (isNull geometry) then
                                include_bounds (geometry.GetBoundingBox true)

            match selection_bounds with
            | ValueSome bounds ->
                Some
                    { target = bounds.Center
                      bounds = ValueSome bounds
                      fallback_distances = ValueNone }
            | ValueNone -> None
        with error ->
            Debug.WriteLine $"RhinosCanFly selection center: {error}"
            None

let selection_center_target (view: RhinoView) (viewport: RhinoViewport) =
    match selection_center_selection view with
    | Some selection when target_is_in_front viewport selection.target -> Some selection.target
    | _ -> None

let prioritized_target
    (mode: PrioritizedTarget)
    (hidden_gumball: Plane voption)
    (view: RhinoView)
    (viewport: RhinoViewport)
    =
    let target =
        match mode with
        | PrioritizedTarget.Gumball ->
            let plane =
                match gumball_plane view with
                | ValueSome plane -> ValueSome plane
                | ValueNone -> hidden_gumball

            match plane with
            | ValueSome plane when plane.IsValid -> Some plane.Origin
            | _ -> None
        | PrioritizedTarget.SelectionCenter -> selection_center_target view viewport
        | PrioritizedTarget.Off
        | _ -> None

    match target with
    | Some point when target_is_in_front viewport point -> Some point
    | Some _
    | None -> None

let selection_filter_allows (enabled: bool) (filter: ObjectType) (rhino_object: RhinoObject) =
    if not enabled then
        true
    else
        filter = ObjectType.AnyObject
        || (rhino_object.ObjectType &&& filter) <> ObjectType.None

let viewport_pick_mode (viewport: RhinoViewport) =
    let display_mode = viewport.DisplayMode

    if not (isNull display_mode) && display_mode.SupportsShading then
        PickMode.Shaded
    else
        PickMode.Wireframe

let create_pick_context
    (view: RhinoView)
    (viewport: RhinoViewport)
    (point: ViewportClientPoint)
    (pick_line: Line)
    (pick_mode: PickMode)
    =
    let context = new PickContext()

    try
        context.View <- view
        context.PickLine <- pick_line
        context.PickStyle <- PickStyle.PointPick
        context.PickMode <- pick_mode
        context.PickGroupsEnabled <- false
        context.SubObjectSelectionEnabled <- false
        context.SetPickTransform(viewport.GetPickTransform(point.x, point.y))
        context.UpdateClippingPlanes()
        context
    with _ ->
        context.Dispose()
        reraise ()

let dispose_object_references (picked: ObjRef array) =
    if not (isNull picked) then
        for reference in picked do
            if not (isNull reference) then
                reference.Dispose()

let picked_target (camera_location: Point3d) (camera_direction: Vector3d) (center: Point3d) (selection_point: Point3d) =
    let center_depth = Vector3d.Multiply(center - camera_location, camera_direction)

    let hit_depth =
        Vector3d.Multiply(selection_point - camera_location, camera_direction)

    let hit_valid = selection_point.IsValid && hit_depth > RhinoMath.ZeroTolerance

    if center.IsValid && center_depth > RhinoMath.ZeroTolerance then
        ValueSome(struct (center, if hit_valid then hit_depth else center_depth))
    elif hit_valid then
        ValueSome(struct (selection_point, hit_depth))
    else
        ValueNone

let object_target
    (camera_location: Point3d)
    (camera_direction: Vector3d)
    (bounds: BoundingBox voption)
    (selection_point: Point3d)
    (approximate: bool)
    =
    let center =
        match bounds with
        | ValueSome bounds -> bounds.Center
        | ValueNone -> Point3d.Unset

    let navigation =
        match picked_target camera_location camera_direction center selection_point with
        | ValueSome(struct (target, _)) ->
            Some
                { target = target
                  source =
                    if approximate then
                        NavigationTargetSource.SelectedBounds
                    elif target = center then
                        NavigationTargetSource.ObjectCenter
                    else
                        NavigationTargetSource.NativeHit }
        | ValueNone -> None

    { navigation = navigation
      bounds = bounds
      native_hit = if approximate then Point3d.Unset else selection_point }

let object_selection (candidate: ObjectTarget) =
    match candidate.bounds with
    | ValueSome bounds ->
        Some
            { target = bounds.Center
              bounds = ValueSome bounds
              fallback_distances = ValueNone }
    | ValueNone ->
        candidate.navigation
        |> Option.map (fun (selection: NavigationSelection) ->
            { target = selection.target
              bounds = ValueNone
              fallback_distances = ValueNone })

let native_candidate
    (camera_location: Point3d)
    (camera_direction: Vector3d)
    (rhino_object: RhinoObject)
    (bounds: BoundingBox voption)
    (hit: Point3d)
    =
    let center =
        match bounds with
        | ValueSome bounds -> bounds.Center
        | ValueNone -> Point3d.Unset

    match picked_target camera_location camera_direction center hit with
    | ValueSome(struct (_, depth)) ->
        ValueSome
            { rhino_object = rhino_object
              hit = hit
              depth = depth }
    | ValueNone -> ValueNone

let pick_batch (view: RhinoView) (context: PickContext) (camera_location: Point3d) (camera_direction: Vector3d) =
    let picked = view.Document.Objects.PickObjects context

    try
        let mutable nearest = ValueNone

        if not (isNull picked) then
            for reference in picked do
                try
                    if not (isNull reference) then
                        let rhino_object = reference.Object()

                        if not (isNull rhino_object) then
                            let hit = reference.SelectionPoint()

                            let bounds =
                                if
                                    hit.IsValid
                                    && Vector3d.Multiply(hit - camera_location, camera_direction) > RhinoMath.ZeroTolerance
                                then
                                    ValueNone
                                else
                                    object_bounds false rhino_object

                            match native_candidate camera_location camera_direction rhino_object bounds hit with
                            | ValueSome candidate ->
                                match nearest with
                                | ValueSome current when current.depth <= candidate.depth -> ()
                                | _ -> nearest <- ValueSome candidate
                            | ValueNone -> ()
                with error ->
                    Debug.WriteLine $"RhinosCanFly pick candidate: {error}"

        nearest
    finally
        dispose_object_references picked

let retry_shaded_pick (mode: PickMode) (candidate: ObjectCandidate voption) (pick: unit -> ObjectCandidate voption) =
    if mode = PickMode.Wireframe && ValueOption.isNone candidate then
        pick ()
    else
        candidate

let selected_object_candidate
    (filter_enabled: bool)
    (geometry_filter: ObjectType)
    (viewport: RhinoViewport)
    (context: PickContext)
    (camera_location: Point3d)
    (camera_direction: Vector3d)
    (rhino_object: RhinoObject)
    =
    if
        isNull rhino_object
        || not rhino_object.Visible
        || not (rhino_object.IsActiveInViewport viewport)
        || not (selection_filter_allows filter_enabled geometry_filter rhino_object)
    then
        ValueNone
    else
        match object_bounds false rhino_object with
        | ValueNone -> ValueNone
        | ValueSome bounds ->
            let center = bounds.Center
            let center_depth = Vector3d.Multiply(center - camera_location, camera_direction)

            if center_depth <= RhinoMath.ZeroTolerance then
                ValueNone
            else
                let mutable completely_inside = false
                let bounds_hit = context.PickFrustumTest(bounds, &completely_inside)

                if not bounds_hit then
                    ValueNone
                else
                    ValueSome
                        { rhino_object = rhino_object
                          depth = center_depth
                          hit = Point3d.Unset }

let query_object_target_candidate_at
    (selected_object_pick_mode: SelectedObjectPickMode)
    (view: RhinoView)
    (viewport: RhinoViewport)
    (point: ViewportClientPoint)
    =
    try
        if isNull view || isNull view.Document then
            Ok None
        else
            let mutable pick_line = Line.Unset

            if not (viewport.GetFrustumLine(float point.x, float point.y, &pick_line)) then
                Ok None
            else
                let filter_enabled = SelectionFilterSettings.Enabled
                let one_shot_filter = SelectionFilterSettings.OneShotGeometryFilter

                let geometry_filter =
                    if one_shot_filter = ObjectType.None then
                        SelectionFilterSettings.GlobalGeometryFilter
                    else
                        one_shot_filter

                let pick_mode = viewport_pick_mode viewport
                use context = create_pick_context view viewport point pick_line pick_mode

                let camera_location = viewport.CameraLocation
                let mutable camera_direction = viewport.CameraDirection
                let mutable selected = ValueNone
                let mutable approximate = false

                if camera_direction.Unitize() then
                    let primary = pick_batch view context camera_location camera_direction

                    selected <-
                        retry_shaded_pick pick_mode primary (fun () ->
                            use shaded_context =
                                create_pick_context view viewport point pick_line PickMode.Shaded

                            pick_batch view shaded_context camera_location camera_direction)

                    // Bounds are useful for approximate navigation, never exact object identity.
                    if
                        selected_object_pick_mode = BoundsWhenNothingElsePicked
                        && ValueOption.isNone selected
                    then
                        approximate <- true
                        // PickObjects can omit selected objects and RhinoCommon has no public per-object picker.
                        for rhino_object in view.Document.Objects.GetSelectedObjects(false, false) do
                            let candidate =
                                selected_object_candidate
                                    filter_enabled
                                    geometry_filter
                                    viewport
                                    context
                                    camera_location
                                    camera_direction
                                    rhino_object

                            match candidate, selected with
                            | ValueSome candidate, ValueSome current when current.depth <= candidate.depth -> ()
                            | ValueSome candidate, _ -> selected <- ValueSome candidate
                            | _ -> ()

                match selected with
                | ValueNone -> Ok None
                | ValueSome candidate ->
                    let bounds = object_bounds true candidate.rhino_object

                    Ok(Some(object_target camera_location camera_direction bounds candidate.hit approximate))
    with error ->
        Error $"RhinosCanFly filtered target: {error}"

let try_object_target_candidate_at
    (selected_object_pick_mode: SelectedObjectPickMode)
    (view: RhinoView)
    (viewport: RhinoViewport)
    (point: ViewportClientPoint)
    =
    query_object_target_candidate_at selected_object_pick_mode view viewport point
    |> target_or_miss

let try_filtered_selection_at (view: RhinoView) (viewport: RhinoViewport) (point: ViewportClientPoint) =
    try_object_target_candidate_at ExactUnderCursor view viewport point
    |> Option.bind object_selection

let try_filtered_target_at (view: RhinoView) (viewport: RhinoViewport) (point: ViewportClientPoint) =
    try_object_target_candidate_at BoundsWhenNothingElsePicked view viewport point
    |> Option.bind (fun (candidate: ObjectTarget) -> candidate.navigation)
    |> Option.map (fun (selection: NavigationSelection) -> selection.target)

let query_object_center_at
    (select: RhinoView -> RhinoViewport -> ViewportClientPoint -> Result<'Target option, string>)
    (view: RhinoView)
    (viewport: RhinoViewport)
    (point: ViewportClientPoint)
    =
    try
        let selection_filter = SelectionFilterSettings.GetCurrentState()

        if
            not selection_filter.Enabled
            && selection_filter.OneShotGeometryFilter = ObjectType.None
            && not selection_filter.SubObjectSelect
        then
            select view viewport point
        else
            try
                SelectionFilterSettings.GlobalGeometryFilter <- ObjectType.AnyObject
                SelectionFilterSettings.OneShotGeometryFilter <- ObjectType.AnyObject
                SelectionFilterSettings.Enabled <- false
                SelectionFilterSettings.SubObjectSelect <- false
                select view viewport point
            finally
                SelectionFilterSettings.UpdateFromState selection_filter
    with error ->
        Error $"RhinosCanFly object center selection: {error}"

let try_object_center_selection_at (view: RhinoView) (viewport: RhinoViewport) (point: ViewportClientPoint) =
    query_object_center_at (query_object_target_candidate_at ExactUnderCursor) view viewport point
    |> target_or_miss
    |> Option.bind object_selection

let try_object_center_target_at (view: RhinoView) (viewport: RhinoViewport) (point: ViewportClientPoint) =
    query_object_center_at (query_object_target_candidate_at BoundsWhenNothingElsePicked) view viewport point
    |> target_or_miss
    |> Option.bind (fun (candidate: ObjectTarget) -> candidate.navigation)
    |> Option.map (fun (selection: NavigationSelection) -> selection.target)

let resolve_distances (config: RetargetConfig) =
    let scaled (RetargetFallbackMultiplier multiplier: RetargetFallbackMultiplier) =
        let distance = config.fallback_distance * multiplier

        if not (RhinoMath.IsValidDouble distance) || distance <= RhinoMath.ZeroTolerance then
            invalidOp "The fallback distance is invalid in this document's units."

        distance

    { perspective = scaled config.perspective_fallback_multiplier
      parallel = scaled config.parallel_fallback_multiplier }

let projection_distance (distances: FallbackDistances) (is_parallel: bool) =
    if is_parallel then
        distances.parallel
    else
        distances.perspective

// Distance is measured along the aim ray, starting at the camera plane.
let distance_point
    (is_parallel: bool)
    (camera: Point3d)
    (forward: Vector3d)
    (ray_origin: Point3d)
    (unit_ray: Vector3d)
    (distance: float)
    =
    let forward_component = Vector3d.Multiply(unit_ray, forward)

    if
        not camera.IsValid
        || not ray_origin.IsValid
        || not unit_ray.IsValid
        || not forward.IsValid
        || abs (unit_ray.SquareLength - 1.) > 1e-6
        || abs (forward.SquareLength - 1.) > 1e-6
        || forward_component <= RhinoMath.ZeroTolerance
        || not (RhinoMath.IsValidDouble distance)
        || distance <= RhinoMath.ZeroTolerance
    then
        invalidOp "The viewport has no usable forward distance ray."

    let origin =
        if is_parallel then
            ray_origin
            - unit_ray * (Vector3d.Multiply(ray_origin - camera, forward) / forward_component)
        else
            camera

    let target = origin + unit_ray * distance

    let depth = Vector3d.Multiply(target - camera, forward)

    if
        not target.IsValid
        || not (RhinoMath.IsValidDouble depth)
        || depth <= RhinoMath.ZeroTolerance
    then
        invalidOp "The fallback target is outside the usable coordinate range."

    target

let distance_selection
    (distances: FallbackDistances)
    (is_parallel: bool)
    (camera: Point3d)
    (forward: Vector3d)
    (ray_origin: Point3d)
    (unit_ray: Vector3d)
    =
    { target = distance_point is_parallel camera forward ray_origin unit_ray (projection_distance distances is_parallel)
      bounds = ValueNone
      fallback_distances = ValueSome distances }

let forward_frustum_ray (forward: Vector3d) (line: Line) (unit_direction: Vector3d) =
    // GetFrustumLine can return far-to-near; aim away from the camera.
    if Vector3d.Multiply(unit_direction, forward) < 0. then
        struct (line.To, -unit_direction)
    else
        struct (line.From, unit_direction)

let distance_selection_at (config: RetargetConfig) (viewport: RhinoViewport) (point: ViewportClientPoint) =
    let distances = resolve_distances config
    let mutable ray = Line.Unset
    let mutable forward = viewport.CameraDirection

    if
        not (viewport.GetFrustumLine(float point.x, float point.y, &ray))
        || not (forward.Unitize())
    then
        invalidOp "The viewport has no valid aiming ray."

    let mutable direction = ray.Direction

    if not (direction.Unitize()) then
        invalidOp "The viewport has no valid aiming direction."

    let struct (origin, forward_direction) = forward_frustum_ray forward ray direction

    Some(
        distance_selection
            distances
            viewport.IsParallelProjection
            viewport.CameraLocation
            forward
            origin
            forward_direction
    )

let distance_target_at (config: RetargetConfig) (viewport: RhinoViewport) (point: ViewportClientPoint) =
    distance_selection_at config viewport point
    |> Option.map (fun (selection: RetargetSelection) -> selection.target)

let retarget_primary_mode (mode: RetargetMode) =
    match mode with
    | RetargetMode.ObjectCenterThenDistance -> struct (RetargetMode.ObjectCenter, true)
    | RetargetMode.TargetThenDistance -> struct (RetargetMode.Target, true)
    | RetargetMode.GeometryThenDistance -> struct (RetargetMode.Geometry, true)
    | RetargetMode.SelectionCenterThenDistance -> struct (RetargetMode.SelectionCenter, true)
    | _ -> struct (mode, false)

let selected_target_at
    (config: RetargetConfig)
    (mode: RetargetMode)
    (view: RhinoView)
    (viewport: RhinoViewport)
    (point: ViewportClientPoint)
    =
    let struct (primary, use_distance) = retarget_primary_mode mode

    let picked =
        match primary with
        | RetargetMode.Distance -> distance_target_at config viewport point
        | RetargetMode.SelectionCenter -> selection_center_target view viewport
        | RetargetMode.Geometry -> try_geometry_target_at viewport point
        | RetargetMode.Target -> try_filtered_target_at view viewport point
        | RetargetMode.ObjectCenter -> try_object_center_target_at view viewport point
        | _ -> None

    match picked with
    | None when use_distance -> distance_target_at config viewport point
    | _ -> picked

let geometry_selection (target: Point3d) (candidate: ObjectTarget option) =
    let bounds =
        match candidate with
        // Only attach bounds when the native object's own hit agrees with the surface sample.
        | Some candidate when
            candidate.native_hit.IsValid
            && target.EpsilonEquals(candidate.native_hit, RhinoMath.ZeroTolerance)
            ->
            candidate.bounds
        | _ -> ValueNone

    { target = target
      bounds = bounds
      fallback_distances = ValueNone }

let try_geometry_selection_at (view: RhinoView) (viewport: RhinoViewport) (point: ViewportClientPoint) =

    match try_geometry_target_at viewport point with
    | None -> None
    | Some target ->
        let candidate =
            query_object_center_at (query_object_target_candidate_at ExactUnderCursor) view viewport point
            |> target_or_miss

        Some(geometry_selection target candidate)

let object_navigation_target
    (navigation_mode: ViewNavigationMode)
    (target_mode: RetargetMode)
    (candidate: ObjectTarget option)
    (query_surface: unit -> Point3d option)
    =
    let navigation =
        candidate |> Option.bind (fun (candidate: ObjectTarget) -> candidate.navigation)

    match navigation with
    | Some selection when selection.source <> NavigationTargetSource.SelectedBounds -> navigation
    | _ when
        navigation_mode = ViewNavigationMode.Pivot
        && target_mode = RetargetMode.ObjectCenter
        ->
        match query_surface () with
        | Some target ->
            Some
                { target = target
                  source = NavigationTargetSource.VisibleSurface }
        | None -> navigation
    | _ -> navigation

let navigation_selection (picked: NavigationSelection option) (existing: Point3d option) =
    match picked, existing with
    | Some selection, _ -> selection
    | None, Some target ->
        { target = target
          source = NavigationTargetSource.ExistingCameraTarget }
    | None, None -> invalidOp "No usable navigation target. Aim at geometry or enable a distance fallback."

let resolve_navigation_target
    (behavior: FlightBehavior)
    (hidden_gumball: Plane voption)
    (mode: ViewNavigationMode)
    (view: RhinoView)
    (viewport: RhinoViewport)
    (point: ViewportClientPoint)
    =
    let requested =
        match mode with
        | ViewNavigationMode.Pivot -> behavior.retarget.on_pivot
        | ViewNavigationMode.Pan -> behavior.retarget.on_pan

    let priority =
        if mode = ViewNavigationMode.Pivot then
            prioritized_target behavior.prioritized_target hidden_gumball view viewport
        else
            None


    let struct (primary_mode, use_distance) = retarget_primary_mode requested

    let target, source =
        match priority with
        | Some target ->
            Some target,
            (if behavior.prioritized_target = PrioritizedTarget.Gumball then
                 NavigationTargetSource.Gumball
             else
                 NavigationTargetSource.SelectionCenter)
        | None ->
            let configured =
                match primary_mode with
                | RetargetMode.Target
                | RetargetMode.ObjectCenter ->
                    let query = query_object_target_candidate_at BoundsWhenNothingElsePicked

                    let candidate =
                        if primary_mode = RetargetMode.ObjectCenter then
                            query_object_center_at query view viewport point
                        else
                            query view viewport point
                        |> target_or_miss

                    object_navigation_target mode primary_mode candidate (fun () ->
                        query_geometry_target_at viewport point |> target_or_miss)
                | _ ->
                    selected_target_at behavior.retarget primary_mode view viewport point
                    |> Option.map (fun (target: Point3d) ->
                        { target = target
                          source =
                            match primary_mode with
                            | RetargetMode.Geometry -> NavigationTargetSource.VisibleSurface
                            | RetargetMode.SelectionCenter -> NavigationTargetSource.SelectionCenter
                            | _ -> NavigationTargetSource.Distance })

            match configured with
            | Some selection -> Some selection.target, selection.source
            | None when use_distance ->
                distance_target_at behavior.retarget viewport point, NavigationTargetSource.Distance
            | None -> None, NavigationTargetSource.ExistingCameraTarget

    let existing =
        if Option.isNone target && target_is_in_front viewport viewport.CameraTarget then
            Some viewport.CameraTarget
        else
            None

    navigation_selection
        (target
         |> Option.map (fun (target: Point3d) -> { target = target; source = source }))
        existing


let apply
    (config: RetargetConfig)
    (mode: RetargetMode)
    (view: RhinoView)
    (viewport: RhinoViewport)
    (can_apply: unit -> bool)
    =
    match selected_target_at config mode view viewport (viewport_center viewport) with
    | Some target when can_apply () -> viewport.SetCameraTarget(target, false)
    | Some _
    | None -> ()

let navigation_camera_target
    (mode: ViewNavigationMode)
    (selection: NavigationSelection)
    (camera: Point3d)
    (forward: Vector3d)
    =
    if
        mode = ViewNavigationMode.Pan
        && selection.source <> NavigationTargetSource.ExistingCameraTarget
    then
        ValueSome(Movement.target_on_camera_axis camera selection.target forward)
    else
        ValueNone

let apply_for_navigation
    (behavior: FlightBehavior)
    (navigation_mode: ViewNavigationMode)
    (view: RhinoView)
    (viewport: RhinoViewport)
    (target_point: NavigationTargetPoint)
    (can_apply: unit -> bool)
    (record_change: Point3d -> Point3d -> unit)
    =
    let point =
        match target_point with
        | NavigationTargetPoint.ClientPoint client_point -> client_point
        | NavigationTargetPoint.ViewCenter -> viewport_center viewport

    let selection =
        resolve_navigation_target behavior ValueNone navigation_mode view viewport point

    if not (can_apply ()) then
        invalidOp "Navigation was cancelled during target acquisition."

    match navigation_camera_target navigation_mode selection viewport.CameraLocation viewport.CameraDirection with
    | ValueSome target ->
        let original = viewport.CameraTarget

        try
            viewport.SetCameraTarget(target, false)
        finally
            if original <> target && viewport.CameraTarget = target then
                record_change original target
    | ValueNone -> ()

    selection.target

let selected_selection_at
    (config: RetargetConfig)
    (mode: RetargetMode)
    (view: RhinoView)
    (viewport: RhinoViewport)
    (point: ViewportClientPoint)
    =

    let struct (primary, use_distance) = retarget_primary_mode mode

    let picked =
        match primary with
        | RetargetMode.Distance -> distance_selection_at config viewport point
        | RetargetMode.SelectionCenter -> selection_center_selection view
        | RetargetMode.Geometry -> try_geometry_selection_at view viewport point
        | RetargetMode.Target -> try_filtered_selection_at view viewport point
        | RetargetMode.ObjectCenter -> try_object_center_selection_at view viewport point
        | _ -> None

    match picked with
    | None when use_distance -> distance_selection_at config viewport point
    | _ -> picked
