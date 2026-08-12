module RhinosCanFly.RuntimeSettings

open System
open System.Collections.Generic
open System.Diagnostics
open Rhino
open Rhino.Commands

let mutable loadedConfig: ConfigLoadResult option = None
let mutable stagedConfig: ConfigLoadResult option = None
let inputSuspensions = Dictionary<int64, InputSuspensionLease>()
let optionsSuspensions = ResizeArray<InputSuspensionLease>()

let current () =
    match loadedConfig with
    | Some loaded -> Ok loaded
    | None -> Error "The configuration has not been loaded. Restart Rhino and try again."

let settings_current () =
    match stagedConfig with
    | Some staged -> Ok staged
    | None -> current ()

let apply_live (loaded: ConfigLoadResult) =
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

let apply (loaded: ConfigLoadResult) =
    if inputSuspensions.Count > 0 then
        Ok()
    else
        apply_live loaded

let suspend_input (reason: InputSuspensionReason) =
    let firstSuspension = inputSuspensions.Count = 0

    if firstSuspension then
        RightClickEntry.suspend ()

    match PlatformInput.suspend_mouse_button_overrides reason with
    | Ok lease ->
        inputSuspensions.Add(lease.id, lease)

        if firstSuspension then
            PlatformInput.record_input_suspended ()

        match lease.cleanup_error with
        | Some error -> RhinoApp.WriteLine $"RhinosCanFly input cleanup is incomplete: {error}"
        | None -> ()

        Ok lease
    | Error error ->
        if firstSuspension then
            RightClickEntry.resume ()

        Error error

let resume_input (lease: InputSuspensionLease) =
    if not (inputSuspensions.ContainsKey lease.id) then
        Ok()
    else
        let lastSuspension = inputSuspensions.Count = 1

        let applyResult =
            if lastSuspension then
                match loadedConfig with
                | Some loaded -> apply_live loaded
                | None -> Ok()
            else
                Ok()

        let resumeResult = PlatformInput.resume_mouse_button_overrides lease
        inputSuspensions.Remove lease.id |> ignore

        if lastSuspension then
            RightClickEntry.resume ()
            PlatformInput.record_input_resumed ()

        match applyResult, resumeResult with
        | Ok(), Ok() -> Ok()
        | Error error, Ok()
        | Ok(), Error error -> Error error
        | Error applyError, Error resumeError -> Error $"{applyError}; {resumeError}"

let candidate (config: FlyConfigFile) =
    let source = ConfigSchema.normalize_numbers config

    match ConfigSchema.compile source with
    | Error error -> Error error
    | Ok runtime ->
        Ok
            { config_file = source
              config = runtime
              messages = [] }

let stage (config: FlyConfigFile) =
    match candidate config with
    | Error error -> Error error
    | Ok requested ->
        stagedConfig <- Some requested
        Ok requested

let discard_staged () = stagedConfig <- None

let commit_staged () =
    match stagedConfig with
    | None -> Ok()
    | Some requested ->
        stagedConfig <- None
        let previous = loadedConfig

        match apply_live requested with
        | Error error ->
            let rollbackError =
                match previous with
                | Some loaded ->
                    match apply_live loaded with
                    | Ok() -> None
                    | Error rollback -> Some rollback
                | None -> None

            match rollbackError with
            | None -> Error error
            | Some rollback -> Error $"{error}; rollback failed: {rollback}"
        | Ok() ->
            match ConfigStorage.save requested.config_file with
            | Ok saved ->
                loadedConfig <- Some saved
                Ok()
            | Error error ->
                let rollbackError =
                    match previous with
                    | Some loaded ->
                        match apply_live loaded with
                        | Ok() -> None
                        | Error rollback -> Some rollback
                    | None -> None

                match rollbackError with
                | None -> Error $"Could not save settings: {error}"
                | Some rollback -> Error $"Could not save settings: {error}; rollback failed: {rollback}"

let rollback_live_settings (previous: ConfigLoadResult option) =
    match previous with
    | Some loaded -> apply_live loaded
    | None -> Ok()

let save_and_apply (config: FlyConfigFile) =
    match candidate config with
    | Error error -> Error error
    | Ok requested ->
        let previous = loadedConfig

        let applyResult = apply_live requested

        match applyResult with
        | Error error ->
            match rollback_live_settings previous with
            | Ok() -> Error error
            | Error rollbackError -> Error $"{error}; rollback failed: {rollbackError}"
        | Ok() ->
            match ConfigStorage.save requested.config_file with
            | Ok saved ->
                loadedConfig <- Some saved
                Ok saved
            | Error error ->
                match rollback_live_settings previous with
                | Ok() -> Error $"Could not save settings: {error}"
                | Error rollbackError -> Error $"Could not save settings: {error}; rollback failed: {rollbackError}"

let load_and_apply () =
    match ConfigStorage.load () with
    | Ok loaded ->
        loadedConfig <- Some loaded
        apply loaded
    | Error error -> Error error

let is_options_command (commandName: string) =
    String.Equals(commandName, "Options", StringComparison.OrdinalIgnoreCase)
    || String.Equals(commandName, "OptionsPage", StringComparison.OrdinalIgnoreCase)
    || String.Equals(commandName, "DocumentProperties", StringComparison.OrdinalIgnoreCase)

let command_began =
    EventHandler<CommandEventArgs>(fun (_: obj) (event: CommandEventArgs) ->
        if is_options_command event.CommandEnglishName then
            match suspend_input InputSuspensionReason.RhinoOptions with
            | Ok lease -> optionsSuspensions.Add lease
            | Error error -> RhinoApp.WriteLine $"RhinosCanFly input suspension failed: {error}")

let command_ended =
    EventHandler<CommandEventArgs>(fun (_: obj) (event: CommandEventArgs) ->
        if is_options_command event.CommandEnglishName then
            if optionsSuspensions.Count > 0 then
                let index = optionsSuspensions.Count - 1
                let lease = optionsSuspensions[index]
                let commitSettings = optionsSuspensions.Count = 1
                optionsSuspensions.RemoveAt index

                let finish () =
                    if commitSettings then
                        match commit_staged () with
                        | Ok() -> ()
                        | Error error -> RhinoApp.WriteLine $"RhinosCanFly settings error: {error}"

                    match resume_input lease with
                    | Ok() -> ()
                    | Error error -> RhinoApp.WriteLine $"RhinosCanFly input resume failed: {error}"

                let mutable handler: EventHandler = null
                let mutable completed = false

                handler <-
                    EventHandler(fun (_: obj) (_: EventArgs) ->
                        try
                            RhinoApp.MainLoop.RemoveHandler handler
                        with error ->
                            Debug.WriteLine $"RhinosCanFly options continuation cleanup: {error.Message}"

                        if not completed then
                            completed <- true
                            finish ())

                try
                    RhinoApp.MainLoop.AddHandler handler
                with error ->
                    Debug.WriteLine $"RhinosCanFly options continuation: {error.Message}"

                    if not completed then
                        completed <- true
                        finish ())

do
    Command.BeginCommand.AddHandler command_began
    Command.EndCommand.AddHandler command_ended

let shutdown () =
    discard_staged ()
    optionsSuspensions.Clear()
    inputSuspensions.Clear()
    Command.BeginCommand.RemoveHandler command_began
    Command.EndCommand.RemoveHandler command_ended
