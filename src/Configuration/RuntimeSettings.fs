module RhinosCanFly.RuntimeSettings

open System
open System.Collections.Generic
open System.Diagnostics
open Rhino

let mutable loadedConfig: ConfigLoadResult option = None
let inputSuspensionIds = HashSet<int64>()
let mutable nextBypassSuspensionId = 0L

[<Literal>]
let input_bypass_experiment = false

let input_bypass_active () = input_bypass_experiment

let record_exception (context: string) (error: exn) =
    let details = $"{DateTimeOffset.Now:O} {context}{Environment.NewLine}{error}"
    Debug.WriteLine details

    try
        RhinoApp.WriteLine $"{context}: {error.Message}"
    with outputError ->
        Debug.WriteLine $"RhinosCanFly exception output failed: {outputError}"

let current () =
    match loadedConfig with
    | Some loaded -> Ok loaded
    | None -> Error "The configuration has not been loaded. Restart Rhino and try again."

let input_suspended () = inputSuspensionIds.Count > 0

let apply_live (loaded: ConfigLoadResult) =
    if input_bypass_experiment then
        let config = loaded.config_file

        let mouseOverrides: MouseOverrideConfig =
            { mouse4 =
                if config.enabled then
                    config.mouse4_pivot_mode
                else
                    MouseButtonPivotMode.Off
              mouse5 =
                if config.enabled then
                    config.mouse5_pivot_mode
                else
                    MouseButtonPivotMode.Off
              shift_right_click =
                if config.enabled then
                    config.shift_right_click_mode
                else
                    ModifiedRightClickMode.Off
              alt_right_click =
                if config.enabled then
                    config.alt_right_click_mode
                else
                    ModifiedRightClickMode.Off
              exit_binding =
                if config.enabled then
                    Some loaded.config.bindings.exit_key
                else
                    None
              exit_on_left = config.enabled && config.exit_on_mouse_left
              exit_on_right = config.enabled && config.exit_on_mouse_right }

        match PlatformInput.apply_mouse_button_overrides mouseOverrides with
        | Error error -> Error error
        | Ok() ->
            RightClickEntry.configure
                { fly_entry_mode =
                    if config.enabled then
                        config.right_click_entry_mode
                    else
                        RightClickEntryMode.Off
                  default_flight_mode = config.default_flight_mode
                  view_manipulation_enabled = PlatformInput.mouse_button_right_click_enabled () }

            Ok()
    else
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

            match PlatformInput.apply_mouse_button_overrides mouseOverrides with
            | Error error -> Error error
            | Ok() ->
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
                Ok()
        with error ->
            Debug.WriteLine $"RhinosCanFly live settings: {error}"
            Error $"Could not apply live settings: {error.Message}"

let suspend_input () =
    if input_bypass_experiment then
        nextBypassSuspensionId <- nextBypassSuspensionId + 1L

        let lease =
            { id = nextBypassSuspensionId
              cleanup_error = None }

        inputSuspensionIds.Add lease.id |> ignore
        Ok lease
    else
        let firstSuspension = inputSuspensionIds.Count = 0

        let rightClickSuspended =
            if firstSuspension then
                try
                    RightClickEntry.suspend ()
                    Ok()
                with error ->
                    record_exception "RhinosCanFly right-click suspension failed" error
                    Error error.Message
            else
                Ok()

        match rightClickSuspended with
        | Error error -> Error error
        | Ok() ->
            let platformResult =
                try
                    PlatformInput.suspend_mouse_button_overrides ()
                with error ->
                    record_exception "RhinosCanFly mouse override suspension failed" error
                    Error error.Message

            match platformResult with
            | Ok lease ->
                inputSuspensionIds.Add lease.id |> ignore

                match lease.cleanup_error with
                | Some error ->
                    try
                        RhinoApp.WriteLine $"RhinosCanFly input cleanup is incomplete: {error}"
                    with outputError ->
                        record_exception "RhinosCanFly input cleanup warning failed" outputError
                | None -> ()

                Ok lease
            | Error error ->
                if firstSuspension then
                    try
                        RightClickEntry.resume ()
                    with resumeError ->
                        record_exception "RhinosCanFly right-click suspension rollback failed" resumeError

                Error error

let resume_input (lease: InputSuspensionLease) =
    if input_bypass_experiment then
        inputSuspensionIds.Remove lease.id |> ignore
        Ok()
    elif not (inputSuspensionIds.Contains lease.id) then
        Ok()
    else
        let lastSuspension = inputSuspensionIds.Count = 1

        let platformResult =
            try
                PlatformInput.resume_mouse_button_overrides lease
            with error ->
                record_exception "RhinosCanFly mouse override resume failed" error
                Error error.Message

        inputSuspensionIds.Remove lease.id |> ignore

        let rightClickResult =
            if lastSuspension && Result.isOk platformResult then
                try
                    RightClickEntry.resume ()
                    Ok()
                with error ->
                    record_exception "RhinosCanFly right-click resume failed" error
                    Error error.Message
            else
                Ok()

        match platformResult, rightClickResult with
        | Ok(), Ok() ->
            if lastSuspension then
                PlatformInput.request_application_redraw ()

            Ok()
        | Error error, Ok()
        | Ok(), Error error -> Error error
        | Error platformError, Error rightClickError -> Error $"{platformError}; {rightClickError}"

let complete_input_recovery () =
    if input_bypass_experiment then
        Ok()
    elif inputSuspensionIds.Count > 0 then
        Error "Input is still suspended by an active command."
    else
        match current () with
        | Error error -> Error error
        | Ok loaded ->
            match apply_live loaded with
            | Error error -> Error error
            | Ok() ->
                try
                    RightClickEntry.resume ()
                    Ok()
                with error ->
                    record_exception "RhinosCanFly right-click recovery failed" error
                    Error error.Message

let candidate (config: FlyConfigFile) =
    let source = ConfigSchema.normalize_numbers config

    match ConfigSchema.compile source with
    | Error error -> Error error
    | Ok runtime ->
        Ok
            { config_file = source
              config = runtime
              messages = [] }

let save_and_apply (config: FlyConfigFile) =
    match candidate config with
    | Error error -> Error error
    | Ok requested ->
        match loadedConfig with
        | None ->
            match ConfigStorage.save requested.config_file with
            | Error error -> Error $"Could not save settings: {error}"
            | Ok saved ->
                match apply_live saved with
                | Ok() ->
                    loadedConfig <- Some saved
                    Ok saved
                | Error error -> Error error
        | Some previous ->
            let rollback (error: string) =
                match apply_live previous with
                | Ok() -> Error error
                | Error rollbackError -> Error $"{error}; rollback failed: {rollbackError}"

            match apply_live requested with
            | Error error -> rollback error
            | Ok() ->
                match ConfigStorage.save requested.config_file with
                | Ok saved ->
                    loadedConfig <- Some saved
                    Ok saved
                | Error error -> rollback $"Could not save settings: {error}"

let load_and_apply () =
    match ConfigStorage.load () with
    | Ok loaded ->
        loadedConfig <- Some loaded

        if inputSuspensionIds.Count > 0 then
            Ok()
        else
            apply_live loaded
    | Error error -> Error error

let shutdown () = inputSuspensionIds.Clear()
