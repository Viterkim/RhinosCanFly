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
    | Mode.Pivot -> PlatformMouseActions.view_latch_is ViewNavigationMode.Pivot
    | Mode.Pan -> PlatformMouseActions.view_latch_is ViewNavigationMode.Pan

let stop (mode: Mode) =
    match mode with
    | Mode.Pivot -> PlatformMouseActions.stop_view_latch ViewNavigationMode.Pivot
    | Mode.Pan -> PlatformMouseActions.stop_view_latch ViewNavigationMode.Pan

let stop_conflict (mode: Mode) =
    match mode with
    | Mode.Pivot -> PlatformMouseActions.stop_view_latch ViewNavigationMode.Pan
    | Mode.Pan -> PlatformMouseActions.stop_view_latch ViewNavigationMode.Pivot

let start (mode: Mode) (view: RhinoView) (completion: Action option) =
    match mode with
    | Mode.Pivot -> PlatformMouseActions.start_view_latch view ViewNavigationMode.Pivot completion
    | Mode.Pan -> PlatformMouseActions.start_view_latch view ViewNavigationMode.Pan completion

let restored_view_completion (loaded: ConfigLoadResult) (view: RhinoView) =
    if DefaultFlightMode.restores_navigation_commands loaded.config_file.default_flight_mode then
        let viewport = view.ActiveViewport
        let snapshot = CameraSnapshot.capture viewport

        try
            let host = PlatformInput.capture_viewport_host view

            let completion =
                Action(fun () ->
                    try
                        if PlatformInput.viewport_host_exists host view then
                            CameraSnapshot.restore viewport snapshot

                            if PlatformInput.viewport_host_is_foreground host view then
                                view.Redraw()
                    finally
                        CameraSnapshot.dispose snapshot)

            struct (Some completion, Some snapshot)
        with _ ->
            CameraSnapshot.dispose snapshot
            reraise ()
    else
        struct (None, None)

let start_navigation (mode: Mode) (loaded: ConfigLoadResult) (view: RhinoView) =
    let command_name = name mode

    match stop_conflict mode with
    | Error error ->
        RhinoApp.WriteLine $"{command_name} failed: {error}"
        Result.Failure
    | Ok() ->
        let struct (completion, snapshot) = restored_view_completion loaded view

        try
            match start mode view completion with
            | Ok() -> Result.Success
            | Error error ->
                snapshot |> Option.iter CameraSnapshot.dispose
                RhinoApp.WriteLine $"{command_name} failed: {error}"
                Result.Failure
        with error ->
            snapshot |> Option.iter CameraSnapshot.dispose
            RhinoApp.WriteLine $"{command_name} failed: {error.Message}"
            Result.Failure

let start_if_ready (mode: Mode) (loaded: ConfigLoadResult) (document: RhinoDoc) =
    let command_name = name mode
    let view = document.Views.ActiveView

    if not (RuntimeSettings.runtime_enabled ()) then
        RhinoApp.WriteLine "RhinosCanFly is disabled."
        Result.Cancel
    elif isNull view then
        RhinoApp.WriteLine $"{command_name}: no active view."
        Result.Failure
    else
        match PlatformInput.cursor_is_over_view view with
        | Error error ->
            RhinoApp.WriteLine $"{command_name} failed: {error}"
            Result.Failure
        | Ok false ->
            RhinoApp.WriteLine $"{command_name}: move the cursor over the active viewport."
            Result.Cancel
        | Ok true -> start_navigation mode loaded view

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
