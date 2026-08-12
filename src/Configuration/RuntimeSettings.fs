module RhinosCanFly.RuntimeSettings

open System
open System.Collections.Generic
open System.Diagnostics
open Rhino

let mutable loadedConfig: ConfigLoadResult option = None
let inputSuspensions = Dictionary<int64, InputSuspensionLease>()

let mutable lastRuntimeSettingsException: string option = None

let record_exception (context: string) (error: exn) =
    let details = $"{DateTimeOffset.Now:O} {context}{Environment.NewLine}{error}"
    lastRuntimeSettingsException <- Some details
    Debug.WriteLine details

    try
        PlatformInput.record_runtime_exception context error
    with diagnosticError ->
        Debug.WriteLine $"RhinosCanFly exception diagnostics failed: {diagnosticError}"

    try
        RhinoApp.WriteLine $"{context}: {error.Message}"
    with outputError ->
        Debug.WriteLine $"RhinosCanFly exception output failed: {outputError}"

let current () =
    match loadedConfig with
    | Some loaded -> Ok loaded
    | None -> Error "The configuration has not been loaded. Restart Rhino and try again."

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

let suspend_input (reason: InputSuspensionReason) =
    let firstSuspension = inputSuspensions.Count = 0

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
                PlatformInput.suspend_mouse_button_overrides reason
            with error ->
                record_exception "RhinosCanFly mouse override suspension failed" error
                Error error.Message

        match platformResult with
        | Ok lease ->
            inputSuspensions.Add(lease.id, lease)

            if firstSuspension then
                PlatformInput.record_input_suspended ()

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
    if not (inputSuspensions.ContainsKey lease.id) then
        Ok()
    else
        let lastSuspension = inputSuspensions.Count = 1

        let platformResult =
            try
                PlatformInput.resume_mouse_button_overrides lease
            with error ->
                record_exception "RhinosCanFly mouse override resume failed" error
                Error error.Message

        inputSuspensions.Remove lease.id |> ignore

        let rightClickResult =
            if lastSuspension && Result.isOk platformResult then
                try
                    RightClickEntry.resume ()
                    PlatformInput.record_input_resumed ()
                    Ok()
                with error ->
                    record_exception "RhinosCanFly right-click resume failed" error
                    Error error.Message
            else
                Ok()

        match platformResult, rightClickResult with
        | Ok(), Ok() -> Ok()
        | Error error, Ok()
        | Ok(), Error error -> Error error
        | Error platformError, Error rightClickError -> Error $"{platformError}; {rightClickError}"

let complete_input_recovery () =
    if inputSuspensions.Count > 0 then
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
                    PlatformInput.record_input_resumed ()
                    Ok()
                with error ->
                    record_exception "RhinosCanFly right-click recovery failed" error
                    Error error.Message

let save_and_apply (config: FlyConfigFile) =
    let source = ConfigSchema.normalize_numbers config

    match loadedConfig with
    | None -> Error "The current configuration is unavailable. Restart Rhino before changing settings."
    | Some previous ->
        match ConfigSchema.compile source with
        | Error error -> Error error
        | Ok runtime ->
            let requested =
                { config_file = source
                  config = runtime
                  messages = [] }

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

        if inputSuspensions.Count > 0 then
            Ok()
        else
            apply_live loaded
    | Error error -> Error error

let diagnostic_lines () =
    let lastException =
        match lastRuntimeSettingsException with
        | Some details -> details
        | None -> "none"

    let activeConfig =
        match loadedConfig with
        | Some loaded ->
            let config = loaded.config_file

            $"Settings config: enabled={config.enabled}; default flight={config.default_flight_mode}; auto target={config.auto_pivot_target_on_exit}; redraw={config.viewport_redraw_mode}; mouse4={config.mouse4_pivot_mode}; mouse5={config.mouse5_pivot_mode}"
        | None -> "Settings config: unavailable"

    [| $"Settings transactions: input suspensions={inputSuspensions.Count}"
       activeConfig
       $"Settings last exception: {lastException}" |]
