module RhinosCanFly.ViewTargetDebug

open System
open System.Diagnostics
open Rhino
open Rhino.Display
open Rhino.Geometry

type CandidateSource =
    | Geometry
    | Distance

type Candidate =
    { source: CandidateSource
      target: Point3d option
      detail: string }

type Evaluation =
    { geometry: Candidate
      distance: Candidate
      selected: Candidate option }

type Snapshot =
    { viewport_id: Guid
      viewport_name: string
      viewport_width: int
      viewport_height: int
      config: ViewTargetConfig
      speed: float
      camera_location: Point3d
      camera_direction: Vector3d
      before_target: Point3d
      evaluation: Evaluation
      after_target: Point3d
      applied: bool }

let mutable displaySnapshot: Snapshot option = None

let source_name (source: CandidateSource) =
    match source with
    | Geometry -> "geometry"
    | Distance -> "distance"

let point_text (point: Point3d) =
    $"({point.X:G9}, {point.Y:G9}, {point.Z:G9})"

let report_line (message: string) =
    try
        RhinoApp.WriteLine message
    with error ->
        Debug.WriteLine $"{message}; output failed: {error.Message}"

type TargetConduit() =
    inherit DisplayConduit()

    let same_point (left: Point3d) (right: Point3d) =
        left.DistanceToSquared right
        <= RhinoMath.ZeroTolerance * RhinoMath.ZeroTolerance

    let candidate_selected (snapshot: Snapshot) (candidate: Candidate) =
        match snapshot.evaluation.selected with
        | Some selected -> selected.source = candidate.source
        | None -> false

    let draw_candidate (args: DrawEventArgs) (snapshot: Snapshot) (candidate: Candidate) =
        match candidate.target with
        | Some target ->
            let selected = candidate_selected snapshot candidate

            let label =
                if selected then
                    $"{source_name candidate.source} (selected)"
                else
                    source_name candidate.source

            args.Display.DrawDot(target, label)
        | None -> ()

    override _.CalculateBoundingBox(args: CalculateBoundingBoxEventArgs) =
        match displaySnapshot with
        | Some current ->
            let mutable bounds = BoundingBox.Empty
            bounds.Union current.before_target

            match current.evaluation.geometry.target with
            | Some target -> bounds.Union target
            | None -> ()

            match current.evaluation.distance.target with
            | Some target -> bounds.Union target
            | None -> ()

            bounds.Union current.after_target
            args.IncludeBoundingBox bounds
        | None -> ()

    override _.DrawForeground(args: DrawEventArgs) =
        match displaySnapshot with
        | Some current when args.Viewport.Id = current.viewport_id ->
            match current.config.mode with
            | ViewTargetMode.Distance -> draw_candidate args current current.evaluation.distance
            | ViewTargetMode.GeometryThenDistance ->
                draw_candidate args current current.evaluation.geometry
                draw_candidate args current current.evaluation.distance
            | ViewTargetMode.Off
            | _ -> ()

            let afterMatchesSelection =
                match current.evaluation.selected with
                | Some selected ->
                    match selected.target with
                    | Some target -> same_point target current.after_target
                    | None -> false
                | None -> false

            if current.applied && not afterMatchesSelection then
                args.Display.DrawDot(current.after_target, "actual target after")
        | Some _
        | None -> ()

let mutable activeDisplay: TargetConduit option = None
let mutable captureEnabled = false

let get_conduit () =
    match activeDisplay with
    | Some current -> current
    | None ->
        let created = new TargetConduit()
        activeDisplay <- Some created
        created

let enabled () = captureEnabled

let set_enabled (enabled: bool) =
    captureEnabled <- enabled

    match activeDisplay with
    | Some current ->
        current.Enabled <- enabled

        if not enabled then
            displaySnapshot <- None
    | None when enabled -> (get_conduit ()).Enabled <- true
    | None -> ()

let report_candidate (candidate: Candidate) =
    match candidate.target with
    | Some target -> report_line $"  {source_name candidate.source}: {point_text target}; {candidate.detail}"
    | None -> report_line $"  {source_name candidate.source}: none; {candidate.detail}"

let record (snapshot: Snapshot) =
    if captureEnabled then
        let current = get_conduit ()
        displaySnapshot <- Some snapshot
        current.Enabled <- true
        let (ViewTargetDistanceMultiplier multiplier) = snapshot.config.distance_multiplier

        report_line "RhinosCanFly target debug"
        report_line $"  mode: {snapshot.config.mode}; applied: {snapshot.applied}"
        report_line $"  speed: {snapshot.speed:G9}; distance multiplier: {multiplier:G9}"

        report_line
            $"  viewport: {snapshot.viewport_name} ({snapshot.viewport_width} x {snapshot.viewport_height}); id: {snapshot.viewport_id}"

        report_line
            $"  camera: {point_text snapshot.camera_location}; direction: ({snapshot.camera_direction.X:G9}, {snapshot.camera_direction.Y:G9}, {snapshot.camera_direction.Z:G9})"

        report_line $"  target before: {point_text snapshot.before_target}"
        report_candidate snapshot.evaluation.geometry
        report_candidate snapshot.evaluation.distance

        match snapshot.evaluation.selected with
        | Some selected -> report_line $"  selected: {source_name selected.source}"
        | None -> report_line "  selected: none"

        report_line $"  target after: {point_text snapshot.after_target}"
        report_line $"  target moved: {snapshot.before_target.DistanceTo snapshot.after_target:G9}"

        match snapshot.evaluation.selected with
        | Some selected ->
            match selected.target with
            | Some target ->
                report_line $"  selected to target-after error: {target.DistanceTo snapshot.after_target:G9}"
            | None -> ()
        | None -> ()

let shutdown () =
    captureEnabled <- false

    match activeDisplay with
    | Some current ->
        current.Enabled <- false
        displaySnapshot <- None
        activeDisplay <- None
    | None -> ()
