module RhinosCanFly.Settings

open System
open Rhino

let current_lens () =
    let document = RhinoDoc.ActiveDoc

    if isNull document || isNull document.Views.ActiveView then
        None
    else
        Some document.Views.ActiveView.ActiveViewport.Camera35mmLensLength

let current_speed (config: FlyConfigFile) =
    let document = RhinoDoc.ActiveDoc

    let range: SpeedRange =
        { minimum = config.minimum_speed
          maximum = config.maximum_speed }

    FlightSpeed.current document config.load_speed_from_document range config.base_speed

let load (control: SettingsControl) =
    control.ShowRuntimeEnabled(RuntimeSettings.runtime_enabled ())

    match RuntimeSettings.current () with
    | Error error ->
        control.LoadConfig ConfigSchema.defaults
        control.ShowRuntimeState(current_speed ConfigSchema.defaults, current_lens ())
        control.ShowError $"Could not load configuration: {error}"
    | Ok result ->
        control.LoadConfig result.config_file

        control.ShowRuntimeState(current_speed result.config_file, current_lens ())

        control.RefreshRawIfVisible()

        let repairMessages =
            result.messages
            |> List.filter (fun (message: string) -> message.StartsWith("reset ", StringComparison.Ordinal))

        match repairMessages with
        | [] -> control.ClearError()
        | messages -> control.ShowError(String.concat "; " messages)

let save (control: SettingsControl) =
    try
        match control.ReadConfig() with
        | Error error ->
            control.ShowError error
            SettingsUi.report_error $"RhinosCanFly settings were not saved: {error}"
            false
        | Ok config ->
            match RuntimeSettings.save_and_apply config with
            | Ok saved ->
                control.RefreshRawIfVisible()
                control.ShowRuntimeState(current_speed saved.config_file, current_lens ())
                control.ShowRuntimeEnabled(RuntimeSettings.runtime_enabled ())
                control.ClearError()
                true
            | Error error ->
                control.ShowError error
                SettingsUi.report_error $"RhinosCanFly settings error: {error}"
                false
    with error ->
        try
            control.ShowError $"Could not save settings: {error.Message}"
        with _ ->
            ()

        SettingsUi.report_error $"RhinosCanFly settings error: {error.Message}"

        false
