module RhinosCanFly.RuntimeSettings

open System
open System.Collections.Generic
open System.Diagnostics
open Rhino

[<RequireQualifiedAccess>]
type RuntimeEnableOverride =
    | FollowConfig
    | ForceEnabled
    | ForceDisabled

let mutable loadedConfig: ConfigLoadResult option = None
let mutable runtimeEnableOverride = RuntimeEnableOverride.FollowConfig
let inputSuspensionIds = HashSet<int64>()

let record_exception (context: string) (error: exn) =
    let details = $"{DateTimeOffset.Now:O} {context}{Environment.NewLine}{error}"
    Debug.WriteLine details

    try
        RhinoApp.WriteLine $"{context}: {error.Message}"
    with outputError ->
        Debug.WriteLine $"RhinosCanFly exception output failed: {outputError}"

let current () =
    match loadedConfig with
    | Some loaded -> Ok loaded
    | None -> Error "The configuration has not been loaded. Restart Rhino and try again."

let input_suspended () = inputSuspensionIds.Count > 0

let runtime_enabled_for (config: FlyConfigFile) =
    match runtimeEnableOverride with
    | RuntimeEnableOverride.FollowConfig -> config.enabled
    | RuntimeEnableOverride.ForceEnabled -> true
    | RuntimeEnableOverride.ForceDisabled -> false

let runtime_enabled () =
    match loadedConfig with
    | Some loaded -> runtime_enabled_for loaded.config_file
    | None -> false

let apply_live (loaded: ConfigLoadResult) =
    try
        let config = loaded.config_file
        let runtime = loaded.config

        let viewNavigationMouse =
            { x_mode = runtime.mouse.x_mode
              y_mode = runtime.mouse.y_mode
              perspective_sensitivity = runtime.mouse.sensitivity
              parallel_sensitivity = runtime.movement.parallel_view.mouse_sensitivity
              perspective_pivot_multiplier = runtime.mouse.pivot_multiplier
              parallel_pivot_multiplier = runtime.movement.parallel_view.mouse_pivot_multiplier
              perspective_pan_multiplier = runtime.mouse.pan_multiplier
              parallel_pan_multiplier = runtime.movement.parallel_view.mouse_pan_multiplier }

        let mouseOverrides: MouseOverrideConfig =
            if runtime_enabled_for config then
                { runtime_enabled = true
                  middle = RoutedMouseAction.create config.middle_mouse_action config.middle_mouse_retarget
                  mouse4 = RoutedMouseAction.create config.mouse4_action config.mouse4_retarget
                  mouse5 = RoutedMouseAction.create config.mouse5_action config.mouse5_retarget
                  right_click_entry = config.right_click_entry_mode
                  default_flight_mode = config.default_flight_mode
                  parallel_view_flying = loaded.config.movement.parallel_view.flying
                  parallel_right_click_entry = loaded.config.movement.parallel_view.right_click_entry
                  shift_right_click =
                    RoutedMouseAction.create config.shift_right_click_action config.shift_right_click_retarget
                  alt_right_click =
                    RoutedMouseAction.create config.alt_right_click_action config.alt_right_click_retarget
                  ctrl_right_click =
                    RoutedMouseAction.create config.ctrl_right_click_action config.ctrl_right_click_retarget
                  exit_binding = Some loaded.config.bindings.exit_key
                  exit_on_left = config.exit_on_mouse_left
                  exit_on_right = config.exit_on_mouse_right
                  outside_flight_cursor =
                    { middle = config.middle_mouse_uses_cursor_outside_flight
                      mouse4 = config.mouse4_uses_cursor_outside_flight
                      mouse5 = config.mouse5_uses_cursor_outside_flight }
                  view_navigation_mouse = viewNavigationMouse
                  prepare_navigation = NavigationTarget.prepare loaded
                  retarget = NavigationTarget.retarget loaded }
            else
                { runtime_enabled = false
                  middle = RoutedMouseAction.Off
                  mouse4 = RoutedMouseAction.Off
                  mouse5 = RoutedMouseAction.Off
                  right_click_entry = RightClickEntryMode.Off
                  default_flight_mode = config.default_flight_mode
                  parallel_view_flying = ParallelViewFlying.DisabledAll
                  parallel_right_click_entry = false
                  shift_right_click = RoutedMouseAction.Off
                  alt_right_click = RoutedMouseAction.Off
                  ctrl_right_click = RoutedMouseAction.Off
                  exit_binding = None
                  exit_on_left = false
                  exit_on_right = false
                  outside_flight_cursor =
                    { middle = false
                      mouse4 = false
                      mouse5 = false }
                  view_navigation_mouse = viewNavigationMouse
                  prepare_navigation = NavigationTarget.prepare loaded
                  retarget = NavigationTarget.retarget loaded }

        match PlatformInput.apply_mouse_button_overrides mouseOverrides with
        | Error error -> Error error
        | Ok() ->
            RepeatBehavior.apply config.commands_do_not_repeat
            Ok()
    with error ->
        Debug.WriteLine $"RhinosCanFly live settings: {error}"
        Error $"Could not apply live settings: {error.Message}"

let toggle_runtime_enabled () =
    match current () with
    | Error error -> Error error
    | Ok loaded ->
        let previousOverride = runtimeEnableOverride

        runtimeEnableOverride <-
            if runtime_enabled_for loaded.config_file then
                RuntimeEnableOverride.ForceDisabled
            else
                RuntimeEnableOverride.ForceEnabled

        let enabled = runtime_enabled_for loaded.config_file

        match apply_live loaded with
        | Ok() -> Ok enabled
        | Error error ->
            runtimeEnableOverride <- previousOverride

            match apply_live loaded with
            | Ok() -> Error error
            | Error rollbackError -> Error $"{error}; rollback failed: {rollbackError}"

let suspend_input () =
    let platformResult =
        try
            PlatformInput.suspend_mouse_button_overrides ()
        with error ->
            record_exception "RhinosCanFly mouse override suspension failed" error
            Error error.Message

    match platformResult with
    | Error error -> Error error
    | Ok lease ->
        inputSuspensionIds.Add lease.id |> ignore

        match lease.cleanup_error with
        | Some error ->
            try
                RhinoApp.WriteLine $"RhinosCanFly input cleanup is incomplete: {error}"
            with outputError ->
                record_exception "RhinosCanFly input cleanup warning failed" outputError
        | None -> ()

        Ok lease

let resume_input (lease: InputSuspensionLease) =
    if not (inputSuspensionIds.Contains lease.id) then
        Ok()
    else
        let lastSuspension = inputSuspensionIds.Count = 1

        let platformResult =
            try
                PlatformInput.resume_mouse_button_overrides lease
            with error ->
                record_exception "RhinosCanFly mouse override resume failed" error
                Error error.Message

        inputSuspensionIds.Remove lease.id |> ignore

        match platformResult with
        | Ok() ->
            if lastSuspension then
                PlatformInput.request_application_redraw ()

            Ok()
        | Error error -> Error error

let complete_input_recovery () =
    if inputSuspensionIds.Count > 0 then
        Error "Input is still suspended by an active command."
    else
        current () |> Result.bind apply_live

let candidate (config: FlyConfigFile) =
    let source = ConfigSchema.normalize config

    match ConfigCompiler.compile source with
    | Error error -> Error error
    | Ok runtime ->
        Ok
            { config_file = source
              config = runtime
              messages = [] }

let save_and_apply (config: FlyConfigFile) =
    match candidate config with
    | Error error -> Error error
    | Ok requested ->
        match loadedConfig with
        | None ->
            match ConfigStorage.save requested.config_file with
            | Error error -> Error $"Could not save settings: {error}"
            | Ok saved ->
                match apply_live saved with
                | Ok() ->
                    loadedConfig <- Some saved
                    Ok saved
                | Error error -> Error error
        | Some previous ->
            let rollback (error: string) =
                match apply_live previous with
                | Ok() -> Error error
                | Error rollbackError -> Error $"{error}; rollback failed: {rollbackError}"

            match apply_live requested with
            | Error error -> rollback error
            | Ok() ->
                match ConfigStorage.save requested.config_file with
                | Ok saved ->
                    loadedConfig <- Some saved
                    Ok saved
                | Error error -> rollback $"Could not save settings: {error}"

let load_and_apply () =
    match ConfigStorage.load () with
    | Ok loaded ->
        loadedConfig <- Some loaded

        if inputSuspensionIds.Count > 0 then
            Ok()
        else
            apply_live loaded
    | Error error -> Error error

let shutdown () = inputSuspensionIds.Clear()
