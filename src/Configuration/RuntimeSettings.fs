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

let mutable loaded_config: ConfigLoadResult option = None
let mutable activation_error: string option = None
let mutable runtime_enable_override = RuntimeEnableOverride.FollowConfig
let input_suspension_ids = HashSet<int64>()
let mutable startup_retry: Eto.Forms.UITimer option = None

let record_exception (context: string) (error: exn) =
    let details = $"{DateTimeOffset.Now:O} {context}{Environment.NewLine}{error}"
    Debug.WriteLine details

    try
        RhinoApp.WriteLine $"{context}: {error.Message}"
    with output_error ->
        Debug.WriteLine $"RhinosCanFly exception output failed: {output_error}"

let current () =
    match loaded_config, activation_error with
    | Some loaded, None -> Ok loaded
    | _, Some error -> Error error
    | None, None -> Error "The configuration has not been loaded. Open RhinosCanFly Options to retry."

let input_suspended () = input_suspension_ids.Count > 0

let runtime_enabled_for (config: FlyConfigFile) =
    match runtime_enable_override with
    | RuntimeEnableOverride.FollowConfig -> config.enabled
    | RuntimeEnableOverride.ForceEnabled -> true
    | RuntimeEnableOverride.ForceDisabled -> false

let runtime_enabled () =
    match loaded_config with
    | Some loaded -> Option.isNone activation_error && runtime_enabled_for loaded.config_file
    | None -> false

let apply_live (loaded: ConfigLoadResult) =
    try
        let config = loaded.config_file
        let runtime = loaded.config

        let view_navigation_mouse =
            { x_mode = runtime.mouse.x_mode
              y_mode = runtime.mouse.y_mode
              perspective_sensitivity = runtime.mouse.sensitivity
              parallel_sensitivity = runtime.movement.parallel_projection.mouse_sensitivity
              perspective_pivot_multiplier = runtime.mouse.pivot_multiplier
              parallel_pivot_multiplier = runtime.movement.parallel_projection.mouse_pivot_multiplier
              perspective_pan_multiplier = runtime.mouse.pan_multiplier
              parallel_pan_multiplier = runtime.movement.parallel_projection.mouse_pan_multiplier }

        let mouse_overrides: MouseOverrideConfig =
            if runtime_enabled_for config then
                { actions =
                    { middle = RoutedMouseAction.create config.middle_mouse_action config.middle_mouse_retarget
                      mouse4 = RoutedMouseAction.create config.mouse4_action config.mouse4_retarget
                      mouse5 = RoutedMouseAction.create config.mouse5_action config.mouse5_retarget
                      right_click_entry = config.right_click_entry_mode
                      default_flight_mode = config.default_flight_mode
                      viewport_capabilities = loaded.config.viewport_access.capabilities
                      right_click_flight_entry = loaded.config.viewport_access.right_click_flight_entry
                      shift_right_click =
                        RoutedMouseAction.create config.shift_right_click_action config.shift_right_click_retarget
                      alt_right_click =
                        RoutedMouseAction.create config.alt_right_click_action config.alt_right_click_retarget
                      ctrl_right_click =
                        RoutedMouseAction.create config.ctrl_right_click_action config.ctrl_right_click_retarget
                      exit_on_mouse_left = config.exit_on_mouse_left
                      exit_on_mouse_right = config.exit_on_mouse_right
                      outside_flight_cursor =
                        { middle = config.middle_mouse_uses_cursor_outside_flight
                          mouse4 = config.mouse4_uses_cursor_outside_flight
                          mouse5 = config.mouse5_uses_cursor_outside_flight }
                      view_navigation_mouse = view_navigation_mouse }
                  exit_binding = Some loaded.config.bindings.exit_key
                  prepare_navigation = NavigationTarget.prepare loaded
                  retarget = NavigationTarget.retarget loaded }
            else
                { actions =
                    { MouseActionConfig.disabled with
                        default_flight_mode = config.default_flight_mode
                        view_navigation_mouse = view_navigation_mouse }
                  exit_binding = None
                  prepare_navigation = NavigationTarget.prepare loaded
                  retarget = NavigationTarget.retarget loaded }

        match PlatformMouseActions.apply mouse_overrides with
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
        let previous_override = runtime_enable_override

        runtime_enable_override <-
            if runtime_enabled_for loaded.config_file then
                RuntimeEnableOverride.ForceDisabled
            else
                RuntimeEnableOverride.ForceEnabled

        let enabled = runtime_enabled_for loaded.config_file

        match apply_live loaded with
        | Ok() -> Ok enabled
        | Error error ->
            runtime_enable_override <- previous_override

            match apply_live loaded with
            | Ok() -> Error error
            | Error rollback_error ->
                activation_error <- Some rollback_error
                Error $"{error}; rollback failed: {rollback_error}"

let suspend_input () =
    let platform_result =
        try
            PlatformMouseActions.suspend ()
        with error ->
            record_exception "RhinosCanFly mouse override suspension failed" error
            Error error.Message

    match platform_result with
    | Error error -> Error error
    | Ok lease ->
        input_suspension_ids.Add lease.id |> ignore

        match lease.cleanup_error with
        | Some error ->
            try
                RhinoApp.WriteLine $"RhinosCanFly input cleanup is incomplete: {error}"
            with output_error ->
                record_exception "RhinosCanFly input cleanup warning failed" output_error
        | None -> ()

        Ok lease

let resume_input (lease: InputSuspensionLease) =
    if not (input_suspension_ids.Contains lease.id) then
        Ok()
    else
        let last_suspension = input_suspension_ids.Count = 1

        let platform_result =
            try
                PlatformMouseActions.resume lease
            with error ->
                record_exception "RhinosCanFly mouse override resume failed" error
                Error error.Message

        input_suspension_ids.Remove lease.id |> ignore

        match platform_result with
        | Ok() ->
            if last_suspension then
                PlatformInput.request_application_redraw ()

            Ok()
        | Error error -> Error error

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
        let previous =
            match loaded_config with
            | Some loaded -> Ok loaded
            | None -> ConfigStorage.load ()

        match previous with
        | Error error -> Error $"Could not load the original settings: {error}"
        | Ok previous ->
            loaded_config <- Some previous

            let rollback (error: string) =
                match apply_live previous with
                | Ok() ->
                    activation_error <- None
                    Error error
                | Error rollback_error ->
                    activation_error <- Some rollback_error
                    Error $"{error}; rollback failed: {rollback_error}"

            match apply_live requested with
            | Error error -> rollback error
            | Ok() ->
                match ConfigStorage.save requested.config_file with
                | Ok saved ->
                    loaded_config <- Some saved
                    activation_error <- None
                    Ok saved
                | Error error -> rollback $"Could not save settings: {error}"

let apply_loaded (result: Result<ConfigLoadResult, string>) =
    match result with
    | Ok loaded ->
        loaded_config <- Some loaded

        match apply_live loaded with
        | Ok() ->
            activation_error <- None
            Ok loaded
        | Error error ->
            activation_error <- Some error
            Error error
    | Error error -> Error error

let reload () = ConfigStorage.load () |> apply_loaded

let complete_input_recovery () =
    if input_suspension_ids.Count > 0 then
        Error "Input is still suspended by an active command."
    else
        match loaded_config with
        | Some loaded -> apply_loaded (Ok loaded) |> Result.map ignore
        | None -> Error "The configuration has not been loaded. Open RhinosCanFly Options to retry."

let resolve () =
    match loaded_config with
    | Some loaded ->
        if Option.isSome activation_error then
            apply_loaded (Ok loaded) |> ignore

        Ok loaded
    | None ->
        let result = ConfigStorage.load ()
        apply_loaded result |> ignore
        result

let initialize () =
    let report_loaded (result: Result<ConfigLoadResult, string>) =
        match apply_loaded result with
        | Ok _ -> ()
        | Error error -> RhinoApp.WriteLine $"RhinosCanFly settings unavailable: {error}"

    match ConfigStorage.try_load () with
    | ValueSome result -> report_loaded result
    | ValueNone ->
        let started = Stopwatch.GetTimestamp()
        let timer = new Eto.Forms.UITimer(Interval = 0.1)
        startup_retry <- Some timer

        timer.Elapsed.Add(fun (_: EventArgs) ->
            try
                let result =
                    if Option.isSome loaded_config then
                        ValueSome(Ok loaded_config.Value)
                    elif float (Stopwatch.GetTimestamp() - started) / float Stopwatch.Frequency >= 2. then
                        ValueSome(Error "Settings remained locked for two seconds. Open Options to retry loading.")
                    else
                        ConfigStorage.try_load ()

                match result with
                | ValueNone -> ()
                | ValueSome result ->
                    timer.Stop()
                    startup_retry <- None
                    timer.Dispose()

                    if Option.isNone loaded_config then
                        report_loaded result
            with error ->
                timer.Stop()
                startup_retry <- None
                timer.Dispose()
                record_exception "RhinosCanFly settings startup retry failed" error)

        timer.Start()

let shutdown () =
    match startup_retry with
    | Some timer ->
        timer.Stop()
        timer.Dispose()
        startup_retry <- None
    | None -> ()

    input_suspension_ids.Clear()
