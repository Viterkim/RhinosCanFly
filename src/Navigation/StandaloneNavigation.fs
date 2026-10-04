module RhinosCanFly.StandaloneNavigation

open System
open Rhino
open Rhino.Commands
open Rhino.Display

let name (mode: ViewNavigationMode) =
    match mode with
    | ViewNavigationMode.Pivot -> "RhinosCanFlyPivot"
    | ViewNavigationMode.Pan -> "RhinosCanFlyPan"

let stop_conflict (mode: ViewNavigationMode) =
    match mode with
    | ViewNavigationMode.Pivot -> PlatformMouseActions.stop_view_latch ViewNavigationMode.Pan
    | ViewNavigationMode.Pan -> PlatformMouseActions.stop_view_latch ViewNavigationMode.Pivot

let restored_view_completion (loaded: ConfigLoadResult) (view: RhinoView) =
    if DefaultFlightMode.restores_navigation_commands loaded.config_file.default_flight_mode then
        let viewport = view.ActiveViewport
        let snapshot = CameraSnapshot.capture viewport

        try
            let host = PlatformInput.capture_viewport_host view
            let mutable completed = false

            let completion =
                Action<unit -> bool>(fun (restore: unit -> bool) ->
                    if not completed then
                        completed <- true

                        try
                            let can_restore () =
                                restore () && PlatformInput.viewport_host_is_foreground host view

                            CameraSnapshot.restore viewport snapshot can_restore

                            if can_restore () then
                                view.Redraw()
                        finally
                            CameraSnapshot.dispose snapshot)

            Some completion
        with _ ->
            CameraSnapshot.dispose snapshot
            reraise ()
    else
        None

let start_navigation (mode: ViewNavigationMode) (loaded: ConfigLoadResult) (view: RhinoView) =
    let command_name = name mode

    match stop_conflict mode with
    | Error error ->
        RhinoApp.WriteLine $"{command_name} failed: {error}"
        Result.Failure
    | Ok() ->
        let completion = restored_view_completion loaded view

        try
            match PlatformMouseActions.start_view_latch view mode completion with
            | Ok() -> Result.Success
            | Error error ->
                completion
                |> Option.iter (fun (complete: Action<unit -> bool>) -> complete.Invoke(fun () -> false))

                RhinoApp.WriteLine $"{command_name} failed: {error}"
                Result.Failure
        with error ->
            completion
            |> Option.iter (fun (complete: Action<unit -> bool>) -> complete.Invoke(fun () -> false))

            RhinoApp.WriteLine $"{command_name} failed: {error.Message}"
            Result.Failure

let start_if_ready (mode: ViewNavigationMode) (loaded: ConfigLoadResult) (document: RhinoDoc) =
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

let toggle (mode: ViewNavigationMode) (document: RhinoDoc) =
    if PlatformMouseActions.view_latch_is mode then
        match PlatformMouseActions.stop_view_latch mode with
        | Ok() -> Result.Success
        | Error error ->
            RhinoApp.WriteLine $"{name mode} failed: {error}"
            Result.Failure
    else
        CurrentConfig.with_loaded (fun (loaded: ConfigLoadResult) -> start_if_ready mode loaded document)

let run (mode: ViewNavigationMode) (document: RhinoDoc) =
    if RuntimeSettings.input_suspended () then
        RhinoApp.WriteLine $"{name mode} is unavailable while an Options dialog is open."
        Result.Cancel
    else
        toggle mode document
