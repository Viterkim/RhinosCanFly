module RhinosCanFly.Settings

open System
open Rhino

let show_raw (control: SettingsControl) =
    match Config.read_raw () with
    | Ok(path, content) -> control.ShowRaw(path, content)
    | Error error -> control.ShowRaw(Config.path (), $"Could not read config: {error}")

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
    match Config.load () with
    | Error error -> control.ShowError $"Could not load configuration: {error}"
    | Ok result ->
        control.LoadConfig result.config_file

        control.ShowRuntimeState(current_speed result.config_file, current_lens ())

        show_raw control

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
        match Config.save config with
        | Ok() ->
            let speed = current_speed config

            let applyResult = LiveSettings.apply_with_speed RhinoDoc.ActiveDoc config speed

            control.ShowRuntimeState(current_speed config, current_lens ())

            show_raw control

            match applyResult with
            | Ok _ -> control.ClearError()
            | Error error -> control.ShowError error

            true
        | Error error ->
            control.ShowError error
            RhinoApp.WriteLine $"RhinosCanFly settings were not saved: {error}"
            false
