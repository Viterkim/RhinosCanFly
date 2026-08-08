module RhinosCanFly.RuntimeSettings

open Rhino

let mutable loadedConfig: ConfigLoadResult option = None

let current () =
    match loadedConfig with
    | Some loaded -> Ok loaded
    | None -> Error "The configuration has not been loaded. Restart Rhino and try again."

let apply (config: FlyConfigFile) =
    let activeConfig =
        if config.enabled then
            config
        else
            { config with
                mouse_button_overrides_enabled = false }

    let mouseButtonResult = PlatformInput.apply_mouse_button_overrides activeConfig

    RightClickEntry.configure
        (config.enabled && config.hijack_right_click_to_enter)
        config.hijack_right_click_during_commands
        (config.enabled && PlatformInput.mouse_button_right_click_enabled ())

    RepeatBehavior.apply config.commands_do_not_repeat
    mouseButtonResult

let apply_speed_and_settings (document: RhinoDoc) (config: FlyConfigFile) (requestedSpeed: float) =
    let speedResult =
        FlightSpeed.set document config.save_speed_to_document config.minimum_speed config.maximum_speed requestedSpeed

    let settingsResult = apply config

    match speedResult, settingsResult with
    | Ok speed, Ok() -> Ok speed
    | Error speedError, Ok() -> Error speedError
    | Ok _, Error settingsError -> Error $"Mouse overrides unavailable: {settingsError}"
    | Error speedError, Error settingsError -> Error $"{speedError}; Mouse overrides unavailable: {settingsError}"

let save (config: FlyConfigFile) =
    match ConfigStorage.save config with
    | Error error -> Error $"Could not save settings: {error}"
    | Ok loaded ->
        loadedConfig <- Some loaded
        Ok loaded

let save_and_apply (config: FlyConfigFile) =
    match save config with
    | Error error -> Error error
    | Ok loaded -> apply loaded.config_file

let save_apply_and_set_speed (document: RhinoDoc) (config: FlyConfigFile) (requestedSpeed: float) =
    match save config with
    | Error error -> Error error
    | Ok loaded -> apply_speed_and_settings document loaded.config_file requestedSpeed

let load_and_apply () =
    match ConfigStorage.load () with
    | Ok loaded ->
        loadedConfig <- Some loaded
        apply loaded.config_file
    | Error error -> Error error
