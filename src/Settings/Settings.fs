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

let load (control: SettingsControl) =
    control.ApplyTheme()

    match Config.load () with
    | Error error -> control.ShowError $"Could not load configuration: {error}"
    | Ok result ->
        control.ClearError()
        control.LoadConfig result.config_file
        control.ShowRuntimeState(Runtime.current_speed result.config_file.base_speed, current_lens ())
        show_raw control

let save (control: SettingsControl) =
    match control.ReadConfig() with
    | Error error ->
        control.ShowError error
        RhinoApp.WriteLine $"RhinosCanFly settings were not saved: {error}"
        false
    | Ok config ->
        match Config.save config with
        | Ok() ->
            Runtime.reset_session_speed config.base_speed
            RightClickEntry.set_enabled config.hijack_right_click_to_enter
            RepeatBehavior.apply config.commands_do_not_repeat
            let mouseOverrideResult = MouseButtonOverrides.apply config
            control.ShowRuntimeState(Runtime.current_speed config.base_speed, current_lens ())
            show_raw control

            match mouseOverrideResult with
            | Ok() -> control.ClearError()
            | Error error -> control.ShowError $"Mouse overrides unavailable: {error}"

            true
        | Error error ->
            control.ShowError error
            RhinoApp.WriteLine $"RhinosCanFly settings were not saved: {error}"
            false
