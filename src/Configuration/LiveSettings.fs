module RhinosCanFly.LiveSettings

open Rhino

let apply (config: FlyConfigFile) =
    RightClickEntry.set_enabled config.hijack_right_click_to_enter
    RepeatBehavior.apply config.commands_do_not_repeat
    MouseButtonOverrides.apply config

let apply_with_speed (document: RhinoDoc) (config: FlyConfigFile) (requestedSpeed: float) =
    let speedResult =
        Runtime.set_speed
            document
            config.save_speed_to_document
            config.minimum_speed
            config.maximum_speed
            requestedSpeed

    let settingsResult = apply config

    match speedResult, settingsResult with
    | Ok speed, Ok() -> Ok speed
    | Error speedError, Ok() -> Error speedError
    | Ok _, Error settingsError -> Error $"Mouse overrides unavailable: {settingsError}"
    | Error speedError, Error settingsError -> Error $"{speedError}; Mouse overrides unavailable: {settingsError}"

let load_and_apply () =
    match Config.load () with
    | Ok loaded -> apply loaded.config_file
    | Error error -> Error error
