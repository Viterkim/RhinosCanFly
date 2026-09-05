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
type RetargetSelection =
    { target: Point3d
      bounds: BoundingBox voption
      used_distance_fallback: bool }

[<Struct>]
type SelectedObjectPickMode =
    | BoundsWhenNothingElsePicked
    | ExactUnderCursor

[<Struct>]
type SelectedObjectCandidate =
    { rhino_object: RhinoObject
      estimated_target: Point3d }

let gumball_target (view: RhinoView) =
    if isNull view || isNull view.Document then
        None
    else
        let mutable plane = Plane.Unset

        if view.Document.GetGumballPlane(&plane) && plane.IsValid && plane.Origin.IsValid then
            Some plane.Origin
        else
            None

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

let try_geometry_target_at (viewport: RhinoViewport) (point: ViewportClientPoint) =
    try
        use capture = new ZBufferCapture(viewport)

        if capture.HitCount() <= 0 then
            None
        else
            let depth = capture.ZValueAt(point.x, point.y)

            if Single.IsNaN depth || Single.IsInfinity depth || depth <= 0.f || depth >= 1.f then
                None
            else
                let target = capture.WorldPointAt(point.x, point.y)

                if target_is_in_front viewport target then
                    Some target
                else
                    None
    with error ->
        Debug.WriteLine $"RhinosCanFly geometry target: {error}"
        None

let try_geometry_target (viewport: RhinoViewport) =
    try_geometry_target_at viewport (viewport_center viewport)

type GeometrySample =
    | NotSampled
    | SampledHit of Point3d
    | SampledMiss

type GeometrySampleState = { mutable sample: GeometrySample }

let geometry_sample () = { sample = NotSampled }

let sampled_geometry_target_at (state: GeometrySampleState) (viewport: RhinoViewport) (point: ViewportClientPoint) =
    match state.sample with
    | SampledHit target -> Some target
    | SampledMiss -> None
    | NotSampled ->
        let result = try_geometry_target_at viewport point

        state.sample <-
            match result with
            | Some target -> SampledHit target
            | None -> SampledMiss

        result

let object_bounds (accurate: bool) (rhino_object: RhinoObject) =
    if isNull rhino_object then
        ValueNone
    else
        let geometry = rhino_object.Geometry

        if isNull geometry then
            ValueNone
        else
            let bounds = geometry.GetBoundingBox accurate

            if bounds.IsValid then ValueSome bounds else ValueNone

let object_selection (viewport: RhinoViewport) (rhino_object: RhinoObject) =
    match object_bounds true rhino_object with
    | ValueSome bounds ->
        let target = bounds.Center

        if target_is_in_front viewport target then
            Some
                { target = target
                  bounds = ValueSome bounds
                  used_distance_fallback = false }
        else
            None
    | ValueNone -> None

let selection_center_selection (view: RhinoView) (viewport: RhinoViewport) =
    if isNull view || isNull view.Document || isNull viewport then
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
            | ValueSome bounds when target_is_in_front viewport bounds.Center ->
                Some
                    { target = bounds.Center
                      bounds = ValueSome bounds
                      used_distance_fallback = false }
            | ValueSome _
            | ValueNone -> None
        with error ->
            Debug.WriteLine $"RhinosCanFly selection center: {error}"
            None

let selection_center_target (view: RhinoView) (viewport: RhinoViewport) =
    match selection_center_selection view viewport with
    | Some selection -> Some selection.target
    | None -> None

let prioritized_target (mode: PrioritizedTarget) (view: RhinoView) (viewport: RhinoViewport) =
    let target =
        match mode with
        | PrioritizedTarget.Gumball -> gumball_target view
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
    context.View <- view
    context.PickLine <- pick_line
    context.PickStyle <- PickStyle.PointPick
    context.PickMode <- pick_mode
    context.PickGroupsEnabled <- false
    context.SubObjectSelectionEnabled <- false
    context.SetPickTransform(viewport.GetPickTransform(point.x, point.y))
    context.UpdateClippingPlanes()
    context

let dispose_object_references (picked: ObjRef array) =
    if not (isNull picked) then
        for reference in picked do
            if not (isNull reference) then
                reference.Dispose()

let picked_object_candidate (camera_location: Point3d) (camera_direction: Vector3d) (reference: ObjRef) =
    if isNull reference then
        ValueNone
    else
        let rhino_object = reference.Object()

        match object_bounds false rhino_object with
        | ValueNone -> ValueNone
        | ValueSome bounds ->
            let center = bounds.Center
            let center_depth = Vector3d.Multiply(center - camera_location, camera_direction)

            if center_depth <= RhinoMath.ZeroTolerance then
                ValueNone
            else
                let selection_point = reference.SelectionPoint()

                let depth =
                    if selection_point.IsValid then
                        let picked_depth =
                            Vector3d.Multiply(selection_point - camera_location, camera_direction)

                        if picked_depth > RhinoMath.ZeroTolerance then
                            picked_depth
                        else
                            center_depth
                    else
                        center_depth

                ValueSome(struct (rhino_object, center, depth))

let selected_object_candidate
    (filter_enabled: bool)
    (geometry_filter: ObjectType)
    (viewport: RhinoViewport)
    (context: PickContext)
    (camera_location: Point3d)
    (camera_direction: Vector3d)
    (geometry_target: Point3d option)
    (geometry_depth: float)
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
                let contains_geometry =
                    match geometry_target with
                    | Some target -> bounds.Contains target
                    | None -> false

                let bounds_hit =
                    match geometry_target with
                    | Some _ -> contains_geometry
                    | None ->
                        let mutable completely_inside = false
                        context.PickFrustumTest(bounds, &completely_inside)

                if not bounds_hit then
                    ValueNone
                else
                    let depth =
                        if contains_geometry && geometry_depth > RhinoMath.ZeroTolerance then
                            geometry_depth
                        else
                            center_depth

                    ValueSome(struct (rhino_object, center, depth))

let try_object_target_candidate_at
    (sample: GeometrySampleState)
    (selected_object_pick_mode: SelectedObjectPickMode)
    (view: RhinoView)
    (viewport: RhinoViewport)
    (point: ViewportClientPoint)
    =
    try
        if isNull view || isNull view.Document then
            None
        else
            let mutable pick_line = Line.Unset

            if not (viewport.GetFrustumLine(float point.x, float point.y, &pick_line)) then
                None
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

                let primary_picked = view.Document.Objects.PickObjects context

                use shaded_context =
                    if
                        pick_mode = PickMode.Wireframe
                        && (isNull primary_picked || primary_picked.Length = 0)
                    then
                        create_pick_context view viewport point pick_line PickMode.Shaded
                    else
                        null

                let picked =
                    if isNull shaded_context then
                        primary_picked
                    else
                        view.Document.Objects.PickObjects shaded_context

                let camera_location = viewport.CameraLocation
                let mutable camera_direction = viewport.CameraDirection
                let mutable nearest_depth = Double.PositiveInfinity
                let mutable selected_object: RhinoObject = null
                let mutable estimated_target = Point3d.Unset

                let consider (candidate: ValueOption<struct (RhinoObject * Point3d * float)>) =
                    match candidate with
                    | ValueSome(struct (rhino_object, target, depth)) when depth < nearest_depth ->
                        nearest_depth <- depth
                        selected_object <- rhino_object
                        estimated_target <- target
                    | ValueSome _
                    | ValueNone -> ()

                try
                    if camera_direction.Unitize() then
                        if not (isNull picked) then
                            for reference in picked do
                                picked_object_candidate camera_location camera_direction reference |> consider

                        let exact_selected_hit = selected_object_pick_mode = ExactUnderCursor
                        let check_selected_objects = exact_selected_hit || isNull selected_object

                        let geometry_target =
                            if check_selected_objects && exact_selected_hit then
                                sampled_geometry_target_at sample viewport point
                            else
                                None

                        let geometry_depth =
                            match geometry_target with
                            | Some target -> Vector3d.Multiply(target - camera_location, camera_direction)
                            | None -> Double.PositiveInfinity

                        if check_selected_objects then
                            // PickObjects can omit selected objects and RhinoCommon has no public per-object picker.
                            for rhino_object in view.Document.Objects.GetSelectedObjects(false, false) do
                                selected_object_candidate
                                    filter_enabled
                                    geometry_filter
                                    viewport
                                    context
                                    camera_location
                                    camera_direction
                                    geometry_target
                                    geometry_depth
                                    rhino_object
                                |> consider

                    if isNull selected_object || not (target_is_in_front viewport estimated_target) then
                        None
                    else
                        Some
                            { rhino_object = selected_object
                              estimated_target = estimated_target }
                finally
                    dispose_object_references picked

                    if not (obj.ReferenceEquals(primary_picked, picked)) then
                        dispose_object_references primary_picked
    with error ->
        Debug.WriteLine $"RhinosCanFly filtered target: {error}"
        None

let try_filtered_selection_at
    (sample: GeometrySampleState)
    (view: RhinoView)
    (viewport: RhinoViewport)
    (point: ViewportClientPoint)
    =
    match try_object_target_candidate_at sample ExactUnderCursor view viewport point with
    | Some candidate ->
        // Framing needs the full box. Navigation can use the cached centre.
        object_selection viewport candidate.rhino_object
    | None -> None

let try_filtered_target_at
    (sample: GeometrySampleState)
    (view: RhinoView)
    (viewport: RhinoViewport)
    (point: ViewportClientPoint)
    =
    match try_object_target_candidate_at sample BoundsWhenNothingElsePicked view viewport point with
    | Some candidate -> Some candidate.estimated_target
    | None -> None

let try_filtered_target (view: RhinoView) (viewport: RhinoViewport) =
    try_filtered_target_at (geometry_sample ()) view viewport (viewport_center viewport)

let try_object_center_at
    (select: RhinoView -> RhinoViewport -> ViewportClientPoint -> 'Target option)
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
        Debug.WriteLine $"RhinosCanFly object center selection: {error}"
        None

let try_object_center_selection_at
    (sample: GeometrySampleState)
    (view: RhinoView)
    (viewport: RhinoViewport)
    (point: ViewportClientPoint)
    =
    try_object_center_at (try_filtered_selection_at sample) view viewport point

let try_object_center_target_at
    (sample: GeometrySampleState)
    (view: RhinoView)
    (viewport: RhinoViewport)
    (point: ViewportClientPoint)
    =
    try_object_center_at (try_filtered_target_at sample) view viewport point

let try_object_center_target (view: RhinoView) (viewport: RhinoViewport) =
    try_object_center_target_at (geometry_sample ()) view viewport (viewport_center viewport)

let retarget_distance (config: RetargetConfig) (speed: float) (viewport: RhinoViewport) =
    let (RetargetFallbackMultiplier multiplier) =
        if viewport.IsParallelProjection then
            config.parallel_fallback_multiplier
        else
            config.perspective_fallback_multiplier

    let distance = speed * multiplier

    if RhinoMath.IsValidDouble distance && distance > RhinoMath.ZeroTolerance then
        Some distance
    else
        None

let parallel_magnification_to_distance (viewport: RhinoViewport) (target: Point3d) (distance: float) =
    if not viewport.IsParallelProjection then
        1.
    else
        let mutable direction = viewport.CameraDirection

        if direction.Unitize() then
            let depth = Vector3d.Multiply(target - viewport.CameraLocation, direction)
            let factor = depth / distance

            if RhinoMath.IsValidDouble factor && factor > RhinoMath.ZeroTolerance then
                factor
            else
                1.
        else
            1.

let distance_target (config: RetargetConfig) (speed: float) (viewport: RhinoViewport) =
    let mutable direction = viewport.CameraDirection

    match retarget_distance config speed viewport with
    | Some distance when direction.Unitize() ->
        let target = viewport.CameraLocation + direction * distance

        if target_is_in_front viewport target then
            Some target
        else
            None
    | Some _
    | None -> None

let distance_target_at (config: RetargetConfig) (speed: float) (viewport: RhinoViewport) (point: ViewportClientPoint) =
    let mutable ray = Line.Unset

    match retarget_distance config speed viewport with
    | Some distance when viewport.GetFrustumLine(float point.x, float point.y, &ray) ->
        let mutable direction = ray.Direction

        if direction.Unitize() then
            let camera = viewport.CameraLocation
            let ray_depth = Vector3d.Multiply(ray.From - camera, direction)

            Some(ray.From + direction * (distance - ray_depth))
        else
            None
    | Some _
    | None -> None

let distance_selection_at
    (config: RetargetConfig)
    (speed: float)
    (viewport: RhinoViewport)
    (point: ViewportClientPoint)
    =
    match distance_target_at config speed viewport point with
    | Some target ->
        Some
            { target = target
              bounds = ValueNone
              used_distance_fallback = true }
    | None -> None

let selected_target_at
    (config: RetargetConfig)
    (mode: RetargetMode)
    (speed: float)
    (view: RhinoView)
    (viewport: RhinoViewport)
    (point: ViewportClientPoint)
    =
    let picked_or_distance (target: Point3d option) =
        match target with
        | Some _ -> target
        | None -> distance_target_at config speed viewport point

    let sample = geometry_sample ()

    match mode with
    | RetargetMode.Distance -> distance_target_at config speed viewport point
    | RetargetMode.SelectionCenter -> selection_center_target view viewport
    | RetargetMode.SelectionCenterThenDistance -> selection_center_target view viewport |> picked_or_distance
    | RetargetMode.Geometry -> sampled_geometry_target_at sample viewport point
    | RetargetMode.GeometryThenDistance -> sampled_geometry_target_at sample viewport point |> picked_or_distance
    | RetargetMode.Target -> try_filtered_target_at sample view viewport point
    | RetargetMode.TargetThenDistance -> try_filtered_target_at sample view viewport point |> picked_or_distance
    | RetargetMode.ObjectCenter -> try_object_center_target_at sample view viewport point
    | RetargetMode.ObjectCenterThenDistance ->
        try_object_center_target_at sample view viewport point |> picked_or_distance
    | RetargetMode.Off
    | _ -> None

let try_geometry_selection_at (view: RhinoView) (viewport: RhinoViewport) (point: ViewportClientPoint) =
    let sample = geometry_sample ()

    match sampled_geometry_target_at sample viewport point with
    | None -> None
    | Some target ->
        let bounds =
            match try_object_center_selection_at sample view viewport point with
            | Some selection -> selection.bounds
            | None -> ValueNone

        Some
            { target = target
              bounds = bounds
              used_distance_fallback = false }

let selected_target
    (config: RetargetConfig)
    (mode: RetargetMode)
    (speed: float)
    (view: RhinoView)
    (viewport: RhinoViewport)
    =
    match mode with
    | RetargetMode.Distance -> distance_target config speed viewport
    | RetargetMode.SelectionCenter -> selection_center_target view viewport
    | RetargetMode.SelectionCenterThenDistance ->
        match selection_center_target view viewport with
        | Some target -> Some target
        | None -> distance_target config speed viewport
    | RetargetMode.Geometry -> try_geometry_target viewport
    | RetargetMode.GeometryThenDistance ->
        match try_geometry_target viewport with
        | Some target -> Some target
        | None -> distance_target config speed viewport
    | RetargetMode.Target -> try_filtered_target view viewport
    | RetargetMode.TargetThenDistance ->
        match try_filtered_target view viewport with
        | Some target -> Some target
        | None -> distance_target config speed viewport
    | RetargetMode.ObjectCenter -> try_object_center_target view viewport
    | RetargetMode.ObjectCenterThenDistance ->
        match try_object_center_target view viewport with
        | Some target -> Some target
        | None -> distance_target config speed viewport
    | RetargetMode.Off
    | _ -> None

let apply (config: RetargetConfig) (mode: RetargetMode) (speed: float) (view: RhinoView) (viewport: RhinoViewport) =
    match selected_target config mode speed view viewport with
    | Some target -> viewport.SetCameraTarget(target, false)
    | None -> ()

let apply_for_navigation
    (config: RetargetConfig)
    (retarget_mode: RetargetMode)
    (navigation_mode: ViewNavigationMode)
    (speed: float)
    (view: RhinoView)
    (viewport: RhinoViewport)
    (target_point: NavigationTargetPoint)
    =
    let point =
        match target_point with
        | NavigationTargetPoint.ClientPoint client_point -> client_point
        | NavigationTargetPoint.ViewCenter -> viewport_center viewport

    match selected_target_at config retarget_mode speed view viewport point with
    | Some selected_target ->
        let target =
            match navigation_mode with
            | ViewNavigationMode.Pivot -> selected_target
            | ViewNavigationMode.Pan ->
                // Keep the picked depth on the camera axis so the first pan frame does not turn the view.
                Movement.target_on_camera_axis viewport.CameraLocation selected_target viewport.CameraDirection

        viewport.SetCameraTarget(target, false)
    | None -> ()

let selected_selection_at
    (config: RetargetConfig)
    (mode: RetargetMode)
    (speed: float)
    (view: RhinoView)
    (viewport: RhinoViewport)
    (point: ViewportClientPoint)
    =
    let sample = geometry_sample ()

    match mode with
    | RetargetMode.Distance -> distance_selection_at config speed viewport point
    | RetargetMode.SelectionCenter -> selection_center_selection view viewport
    | RetargetMode.SelectionCenterThenDistance ->
        match selection_center_selection view viewport with
        | Some selection -> Some selection
        | None -> distance_selection_at config speed viewport point
    | RetargetMode.Geometry -> try_geometry_selection_at view viewport point
    | RetargetMode.GeometryThenDistance ->
        match try_geometry_selection_at view viewport point with
        | Some selection -> Some selection
        | None -> distance_selection_at config speed viewport point
    | RetargetMode.Target -> try_filtered_selection_at sample view viewport point
    | RetargetMode.TargetThenDistance ->
        match try_filtered_selection_at sample view viewport point with
        | Some selection -> Some selection
        | None -> distance_selection_at config speed viewport point
    | RetargetMode.ObjectCenter -> try_object_center_selection_at sample view viewport point
    | RetargetMode.ObjectCenterThenDistance ->
        match try_object_center_selection_at sample view viewport point with
        | Some selection -> Some selection
        | None -> distance_selection_at config speed viewport point
    | RetargetMode.Off
    | _ -> None
