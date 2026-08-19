module RhinosCanFly.StandaloneNavigation

open System
open Rhino
open Rhino.Commands
open Rhino.Display

[<RequireQualifiedAccess>]
type Mode =
    | Pivot
    | Pan

let name (mode: Mode) =
    match mode with
    | Mode.Pivot -> "RhinosCanFlyPivot"
    | Mode.Pan -> "RhinosCanFlyPan"

let active (mode: Mode) =
    match mode with
    | Mode.Pivot -> PlatformInput.pivot_active ()
    | Mode.Pan -> PlatformInput.pan_active ()

let stop (mode: Mode) =
    match mode with
    | Mode.Pivot -> PlatformInput.stop_pivot ()
    | Mode.Pan -> PlatformInput.stop_pan ()

let stop_conflict (mode: Mode) =
    match mode with
    | Mode.Pivot -> PlatformInput.stop_pan ()
    | Mode.Pan -> PlatformInput.stop_pivot ()

let start (mode: Mode) (view: RhinoView) (completion: Action option) =
    match mode with
    | Mode.Pivot -> PlatformInput.start_pivot view completion
    | Mode.Pan -> PlatformInput.start_pan view completion

let restored_view_completion (loaded: ConfigLoadResult) (view: RhinoView) =
    if DefaultFlightMode.restores_navigation_commands loaded.config_file.default_flight_mode then
        let viewport = view.ActiveViewport
        let snapshot = CameraSnapshot.capture viewport
        let host = PlatformInput.capture_viewport_host view

        Some(
            Action(fun () ->
                if PlatformInput.viewport_host_exists host view then
                    CameraSnapshot.restore viewport snapshot

                    if PlatformInput.viewport_host_is_foreground host view then
                        view.Redraw())
        )
    else
        None

let apply_view_target (loaded: ConfigLoadResult) (document: RhinoDoc) (view: RhinoView) =
    let behavior = loaded.config.behavior
    let movement = loaded.config.movement

    let speed =
        FlightSpeed.current document behavior.load_speed_from_document movement.speed_range movement.base_speed

    ViewTarget.apply behavior.view_target speed view.ActiveViewport

let start_navigation (mode: Mode) (loaded: ConfigLoadResult) (document: RhinoDoc) (view: RhinoView) =
    let commandName = name mode

    match stop_conflict mode with
    | Error error ->
        RhinoApp.WriteLine $"{commandName} failed: {error}"
        Result.Failure
    | Ok() ->
        let completion = restored_view_completion loaded view

        match start mode view completion with
        | Ok() ->
            try
                apply_view_target loaded document view
                Result.Success
            with error ->
                match stop mode with
                | Ok() -> RhinoApp.WriteLine $"{commandName} failed to set the view target: {error.Message}"
                | Error cleanupError ->
                    RhinoApp.WriteLine
                        $"{commandName} failed to set the view target: {error.Message} Cleanup also failed: {cleanupError}"

                Result.Failure
        | Error error ->
            RhinoApp.WriteLine $"{commandName} failed: {error}"
            Result.Failure

let start_if_ready (mode: Mode) (loaded: ConfigLoadResult) (document: RhinoDoc) =
    let commandName = name mode
    let view = document.Views.ActiveView

    if not (RuntimeSettings.runtime_enabled ()) then
        RhinoApp.WriteLine "RhinosCanFly is disabled."
        Result.Cancel
    elif isNull view then
        RhinoApp.WriteLine $"{commandName}: no active view."
        Result.Failure
    else
        match PlatformInput.cursor_is_over_view view with
        | Error error ->
            RhinoApp.WriteLine $"{commandName} failed: {error}"
            Result.Failure
        | Ok false ->
            RhinoApp.WriteLine $"{commandName}: move the cursor over the active viewport."
            Result.Cancel
        | Ok true -> start_navigation mode loaded document view

let toggle (mode: Mode) (document: RhinoDoc) =
    if active mode then
        match stop mode with
        | Ok() -> Result.Success
        | Error error ->
            RhinoApp.WriteLine $"{name mode} failed: {error}"
            Result.Failure
    else
        CurrentConfig.with_loaded (fun (loaded: ConfigLoadResult) -> start_if_ready mode loaded document)

let run (mode: Mode) (document: RhinoDoc) =
    if RuntimeSettings.input_suspended () then
        RhinoApp.WriteLine $"{name mode} is unavailable while an Options dialog is open."
        Result.Cancel
    else
        toggle mode document
