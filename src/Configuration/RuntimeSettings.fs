module RhinosCanFly.RuntimeSettings

open System.Diagnostics
open Rhino

let mutable loadedConfig: ConfigLoadResult option = None

let current () =
    match loadedConfig with
    | Some loaded -> Ok loaded
    | None -> Error "The configuration has not been loaded. Restart Rhino and try again."

let apply (loaded: ConfigLoadResult) =
    try
        let config = loaded.config_file

        let mouseOverrides: MouseOverrideConfig =
            if config.enabled then
                { mouse4 = config.mouse4_pivot_mode
                  mouse5 = config.mouse5_pivot_mode
                  shift_right_click = config.shift_right_click_mode
                  alt_right_click = config.alt_right_click_mode
                  exit_binding = Some loaded.config.bindings.exit_key
                  exit_on_left = config.exit_on_mouse_left
                  exit_on_right = config.exit_on_mouse_right }
            else
                { mouse4 = MouseButtonPivotMode.Off
                  mouse5 = MouseButtonPivotMode.Off
                  shift_right_click = ModifiedRightClickMode.Off
                  alt_right_click = ModifiedRightClickMode.Off
                  exit_binding = None
                  exit_on_left = false
                  exit_on_right = false }

        let mouseButtonResult = PlatformInput.apply_mouse_button_overrides mouseOverrides

        let flyEntryMode =
            if config.enabled then
                config.right_click_entry_mode
            else
                RightClickEntryMode.Off

        RightClickEntry.configure
            { fly_entry_mode = flyEntryMode
              default_flight_mode = config.default_flight_mode
              view_manipulation_enabled = PlatformInput.mouse_button_right_click_enabled () }

        RepeatBehavior.apply config.commands_do_not_repeat
        mouseButtonResult
    with error ->
        Debug.WriteLine $"RhinosCanFly live settings: {error}"
        Error $"Could not apply live settings: {error.Message}"

let apply_speed_and_settings (document: RhinoDoc) (loaded: ConfigLoadResult) (requestedSpeed: float) =
    let config = loaded.config_file
    let movement = loaded.config.movement

    let speedResult =
        FlightSpeed.set document config.save_speed_to_document movement.speed_range requestedSpeed

    let settingsResult = apply loaded

    match speedResult, settingsResult with
    | Ok speed, Ok() -> Ok speed
    | Error speedError, Ok() -> Error speedError
    | Ok _, Error settingsError -> Error settingsError
    | Error speedError, Error settingsError -> Error $"{speedError}; {settingsError}"

let save (config: FlyConfigFile) =
    match ConfigStorage.save config with
    | Error error -> Error $"Could not save settings: {error}"
    | Ok loaded ->
        loadedConfig <- Some loaded
        Ok loaded

let save_and_apply (config: FlyConfigFile) =
    match save config with
    | Error error -> Error error
    | Ok loaded -> apply loaded

let save_apply_and_set_speed (document: RhinoDoc) (config: FlyConfigFile) (requestedSpeed: float) =
    match save config with
    | Error error -> Error error
    | Ok loaded -> apply_speed_and_settings document loaded requestedSpeed

let load_and_apply () =
    match ConfigStorage.load () with
    | Ok loaded ->
        loadedConfig <- Some loaded
        apply loaded
    | Error error -> Error error
