module RhinosCanFly.ExitPivotTargetDebug

open System
open System.Diagnostics
open System.Drawing
open Rhino
open Rhino.Display
open Rhino.Geometry

type CandidateSource =
    | Geometry
    | ConstructionPlane
    | WorldXY

type Candidate =
    { source: CandidateSource
      target: Point3d option
      detail: string }

type Snapshot =
    { viewport_id: Guid
      viewport_name: string
      viewport_width: int
      viewport_height: int
      mode: ExitPivotTargetMode
      camera_location: Point3d
      camera_direction: Vector3d
      before_target: Point3d
      geometry: Candidate
      construction_plane: Candidate
      world_xy: Candidate
      selected: Candidate option
      after_target: Point3d
      applied: bool }

let mutable displaySnapshot: Snapshot option = None

let source_name (source: CandidateSource) =
    match source with
    | Geometry -> "geometry"
    | ConstructionPlane -> "CPlane"
    | WorldXY -> "World XY"

let point_text (point: Point3d) =
    $"({point.X:G9}, {point.Y:G9}, {point.Z:G9})"

let report_line (message: string) =
    try
        RhinoApp.WriteLine message
    with error ->
        Debug.WriteLine $"{message}; output failed: {error.Message}"

type TargetConduit() =
    inherit DisplayConduit()

    let draw_candidate (args: DrawEventArgs) (candidate: Candidate) (color: Color) =
        match candidate.target with
        | Some target -> args.Display.DrawDot(target, source_name candidate.source, color, Color.Black)
        | None -> ()

    override _.CalculateBoundingBox(args: CalculateBoundingBoxEventArgs) =
        match displaySnapshot with
        | Some current ->
            let mutable bounds = BoundingBox.Empty
            bounds.Union current.before_target

            match current.geometry.target with
            | Some target -> bounds.Union target
            | None -> ()

            match current.construction_plane.target with
            | Some target -> bounds.Union target
            | None -> ()

            match current.world_xy.target with
            | Some target -> bounds.Union target
            | None -> ()

            bounds.Union current.after_target
            args.IncludeBoundingBox bounds
        | None -> ()

    override _.DrawForeground(args: DrawEventArgs) =
        match displaySnapshot with
        | Some current when args.Viewport.Id = current.viewport_id ->
            args.Display.DrawDot(current.before_target, "target before", Color.White, Color.Black)
            draw_candidate args current.geometry Color.LimeGreen
            draw_candidate args current.construction_plane Color.DeepSkyBlue
            draw_candidate args current.world_xy Color.Magenta

            match current.selected with
            | Some selected ->
                match selected.target with
                | Some target ->
                    args.Display.DrawDot(target, $"selected: {source_name selected.source}", Color.Yellow, Color.Black)
                | None -> ()
            | None -> ()

            if current.after_target.DistanceToSquared current.before_target > RhinoMath.ZeroTolerance then
                args.Display.DrawDot(current.after_target, "target after", Color.Orange, Color.Black)
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

        report_line "RhinosCanFly pivot-target debug"
        report_line $"  mode: {snapshot.mode}; applied: {snapshot.applied}"

        report_line
            $"  viewport: {snapshot.viewport_name} ({snapshot.viewport_width} x {snapshot.viewport_height}); id: {snapshot.viewport_id}"

        report_line
            $"  camera: {point_text snapshot.camera_location}; direction: ({snapshot.camera_direction.X:G9}, {snapshot.camera_direction.Y:G9}, {snapshot.camera_direction.Z:G9})"

        report_line $"  target before: {point_text snapshot.before_target}"
        report_candidate snapshot.geometry
        report_candidate snapshot.construction_plane
        report_candidate snapshot.world_xy

        match snapshot.selected with
        | Some selected -> report_line $"  selected: {source_name selected.source}"
        | None -> report_line "  selected: none"

        report_line $"  target after: {point_text snapshot.after_target}"
        report_line $"  target moved: {snapshot.before_target.DistanceTo snapshot.after_target:G9}"

        match snapshot.selected with
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
