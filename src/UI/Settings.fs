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

let refresh_runtime (config: FlyConfigFile) (control: SettingsControl) =
    control.ShowRuntimeEnabled(RuntimeSettings.runtime_enabled ())
    control.ShowRuntimeState(current_speed config, current_lens ())

let load (loaded: Result<ConfigLoadResult, string>) (control: SettingsControl) =

    match loaded with
    | Error error ->
        control.LoadConfig ConfigSchema.defaults
        refresh_runtime ConfigSchema.defaults control
        control.ShowError $"Could not load configuration: {error}"
    | Ok result ->
        control.LoadConfig result.config_file

        refresh_runtime result.config_file control

        control.RefreshRawIfVisible()

        let repair_messages =
            result.messages
            |> List.filter (fun (message: string) -> message.StartsWith("reset ", StringComparison.Ordinal))

        match RuntimeSettings.activation_error, repair_messages with
        | Some error, _ -> control.ShowError $"Settings loaded, but input could not be activated: {error}"
        | None, [] -> control.ClearError()
        | None, messages -> control.ShowError(String.concat "; " messages)

let needs_save (displayed: FlyConfigFile option) (defaults_requested: bool) (edited: FlyConfigFile) =
    defaults_requested
    || Option.map ConfigSchema.normalize displayed
       <> Some(ConfigSchema.normalize edited)

let save (revision: string) (control: SettingsControl) (edited: Result<FlyConfigFile, string>) =
    try
        match edited with
        | Error error ->
            control.ShowError error
            SettingsUi.report_error $"RhinosCanFly settings were not saved: {error}"
            None
        | Ok config ->
            match RuntimeSettings.save_and_apply revision config with
            | Ok saved -> Some saved
            | Error error ->
                control.ShowError error
                SettingsUi.report_error $"RhinosCanFly settings error: {error}"
                None
    with error ->
        try
            control.ShowError $"Could not save settings: {error.Message}"
        with _ ->
            ()

        SettingsUi.report_error $"RhinosCanFly settings error: {error.Message}"

        None
