module RhinosCanFly.RuntimeSettings

open Rhino

let mutable loadedConfig: ConfigLoadResult option = None

let current () =
    match loadedConfig with
    | Some loaded -> Ok loaded
    | None -> Error "The configuration has not been loaded. Restart Rhino and try again."

let apply (loaded: ConfigLoadResult) =
    let config = loaded.config_file

    let activeConfig =
        if config.enabled then
            config
        else
            { config with
                mouse4_pivot_mode = MouseButtonPivotMode.Off
                mouse5_pivot_mode = MouseButtonPivotMode.Off
                shift_right_click_mode = ModifiedRightClickMode.Off
                alt_right_click_mode = ModifiedRightClickMode.Off
                exit_on_mouse_left = false
                exit_on_mouse_right = false }

    let mouseButtonResult =
        PlatformInput.apply_mouse_button_overrides activeConfig loaded.config.exit_key

    let entryEnabled, enterDuringCommands, entrySessionMode =
        match config.right_click_entry_mode with
        | RightClickEntryMode.Off -> false, false, FlightSessionMode.Persistent
        | RightClickEntryMode.EnterFlying -> true, false, FlightSessionMode.Persistent
        | RightClickEntryMode.EnterFlyingDuringCommands -> true, true, FlightSessionMode.Persistent
        | RightClickEntryMode.EnterFlyingWhileHeld -> true, false, FlightSessionMode.WhileRightMouseHeld
        | RightClickEntryMode.EnterFlyingWhileHeldDuringCommands -> true, true, FlightSessionMode.WhileRightMouseHeld
        | _ -> false, false, FlightSessionMode.Persistent

    RightClickEntry.configure
        (config.enabled && entryEnabled)
        enterDuringCommands
        entrySessionMode
        (PlatformInput.mouse_button_right_click_enabled ())

    RepeatBehavior.apply config.commands_do_not_repeat
    mouseButtonResult

let apply_speed_and_settings (document: RhinoDoc) (loaded: ConfigLoadResult) (requestedSpeed: float) =
    let config = loaded.config_file

    let speedResult =
        FlightSpeed.set document config.save_speed_to_document config.minimum_speed config.maximum_speed requestedSpeed

    let settingsResult = apply loaded

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
