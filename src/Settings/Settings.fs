module RhinosCanFly.Settings

open System
open Rhino

let current_lens () =
    let document = RhinoDoc.ActiveDoc

    if isNull document || isNull document.Views.ActiveView then
        None
    else
        let viewport = document.Views.ActiveView.ActiveViewport

        if viewport.IsParallelProjection then
            None
        else
            Some viewport.Camera35mmLensLength

let current_speed (config: FlyConfigFile) =
    let document = RhinoDoc.ActiveDoc

    let range: SpeedRange =
        { minimum = config.minimum_speed
          maximum = config.maximum_speed }

    FlightSpeed.current document config.load_speed_from_document range config.base_speed

let load (control: SettingsControl) =
    InputDebugTrace.write "Settings.load begin"
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

    InputDebugTrace.write "Settings.load end"

let save (control: SettingsControl) =
    InputDebugTrace.write "Settings.save begin"

    try
        match control.ReadConfig() with
        | Error error ->
            InputDebugTrace.write $"Settings.save config read result=error error={error}"
            control.ShowError error
            SettingsUi.report_error $"RhinosCanFly settings were not saved: {error}"
            false
        | Ok config ->
            InputDebugTrace.write "Settings.save config read result=ok save-and-apply begin"

            match RuntimeSettings.save_and_apply config with
            | Ok saved ->
                InputDebugTrace.write "Settings.save save-and-apply end result=ok control refresh begin"
                control.RefreshRawIfVisible()
                control.ShowRuntimeState(current_speed saved.config_file, current_lens ())
                control.ShowRuntimeEnabled(RuntimeSettings.runtime_enabled ())
                control.ClearError()
                InputDebugTrace.write "Settings.save end result=true"
                true
            | Error error ->
                InputDebugTrace.write $"Settings.save save-and-apply end result=error error={error}"
                control.ShowError error
                SettingsUi.report_error $"RhinosCanFly settings error: {error}"
                false
    with error ->
        InputDebugTrace.write $"Settings.save exception={error}"

        try
            control.ShowError $"Could not save settings: {error.Message}"
        with _ ->
            ()

        SettingsUi.report_error $"RhinosCanFly settings error: {error.Message}"

        false
