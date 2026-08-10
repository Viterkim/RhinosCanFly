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

    FlightSpeed.current
        document
        config.load_speed_from_document
        config.minimum_speed
        config.maximum_speed
        config.base_speed

let load (control: SettingsControl) =
    match RuntimeSettings.current () with
    | Error error -> control.ShowError $"Could not load configuration: {error}"
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
    match control.ReadConfig() with
    | Error error ->
        control.ShowError error
        RhinoApp.WriteLine $"RhinosCanFly settings were not saved: {error}"
        false
    | Ok config ->
        let result = RuntimeSettings.save_and_apply config
        control.RefreshRawIfVisible()

        match result with
        | Ok _ ->
            control.ShowRuntimeState(current_speed config, current_lens ())
            control.ClearError()
            true
        | Error error ->
            control.ShowError error
            RhinoApp.WriteLine $"RhinosCanFly settings error: {error}"
            false
