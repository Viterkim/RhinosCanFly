module RhinosCanFly.RuntimeSettings

open Rhino

let apply (config: FlyConfigFile) =
    RightClickEntry.configure config.hijack_right_click_to_enter config.hijack_right_click_during_commands
    RepeatBehavior.apply config.commands_do_not_repeat
    PlatformInput.apply_mouse_button_overrides config

let apply_speed_and_settings (document: RhinoDoc) (config: FlyConfigFile) (requestedSpeed: float) =
    let speedResult =
        FlightSpeed.set document config.save_speed_to_document config.minimum_speed config.maximum_speed requestedSpeed

    let settingsResult = apply config

    match speedResult, settingsResult with
    | Ok speed, Ok() -> Ok speed
    | Error speedError, Ok() -> Error speedError
    | Ok _, Error settingsError -> Error $"Mouse overrides unavailable: {settingsError}"
    | Error speedError, Error settingsError -> Error $"{speedError}; Mouse overrides unavailable: {settingsError}"

let save_and_apply (document: RhinoDoc) (config: FlyConfigFile) (requestedSpeed: float) =
    match Config.save config with
    | Error error -> Error $"Could not save settings: {error}"
    | Ok() -> apply_speed_and_settings document config requestedSpeed

let load_and_apply () =
    match Config.load () with
    | Ok loaded -> apply loaded.config_file
    | Error error -> Error error
