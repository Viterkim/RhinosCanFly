open System
open System.IO
open System.Text.Json.Nodes
open RhinosCanFly

let fail (message: string) =
    failwith $"Config recovery check failed: {message}"

let require (condition: bool) (message: string) =
    if not condition then
        fail message

let backup_count (directory: string) =
    Directory.EnumerateFiles(directory, "rhinos-can-fly-config.backup-*.json")
    |> Seq.length

let backup_contents (directory: string) =
    Directory.EnumerateFiles(directory, "rhinos-can-fly-config.backup-*.json")
    |> Seq.map File.ReadAllText
    |> List.ofSeq

let write_document (config_path: string) (json: JsonObject) =
    File.WriteAllText(config_path, ConfigDocument.content json)

let set_value (json: JsonObject) (name: string) (value: JsonNode) = json[name] <- value

let temporary_directory =
    Path.Combine(Path.GetTempPath(), $"rhinos-can-fly-config-check-{Guid.NewGuid():N}")

try
    let previous_version = ConfigDocument.to_object ConfigSchema.defaults
    set_value previous_version "config_version" (JsonValue.Create(ConfigSchema.CURRENT_VERSION - 1))
    set_value previous_version "base_speed" (JsonValue.Create 43.)
    previous_version.Remove "right_click_flight_entry" |> ignore
    set_value previous_version "removed_setting" (JsonValue.Create true)

    match ConfigRepair.repair_document previous_version with
    | Error error -> fail error
    | Ok repaired ->
        require repaired.changed "previous config version was not migrated"
        require (repaired.config_file.config_version = ConfigSchema.CURRENT_VERSION) "migrated config version"
        require (repaired.config_file.base_speed = 43.) "migration did not preserve a known setting"

        require
            (repaired.config_file.right_click_flight_entry = ConfigSchema.defaults.right_click_flight_entry)
            "migration did not add a missing setting with its default"

        require (not (repaired.document.ContainsKey "removed_setting")) "migration kept an unknown setting"

    let empty_nested = ConfigDocument.to_object ConfigSchema.defaults
    set_value empty_nested "viewport_capabilities" (JsonObject())

    match ConfigRepair.repair_document empty_nested with
    | Error error -> fail error
    | Ok repaired ->
        require
            (repaired.config_file.viewport_capabilities = ConfigSchema.defaults.viewport_capabilities)
            "empty nested object did not receive its schema defaults"

    let partial_nested = ConfigDocument.to_object ConfigSchema.defaults
    let partial_list = JsonObject()
    partial_list["viewports"] <- JsonArray(JsonValue.Create "Perspective")
    set_value partial_nested "viewport_capabilities" partial_list

    match ConfigRepair.repair_document partial_nested with
    | Error error -> fail error
    | Ok repaired ->
        require
            (repaired.config_file.viewport_capabilities.mode = ConfigSchema.defaults.viewport_capabilities.mode)
            "omitted nested mode did not receive its schema default"

        require
            (repaired.config_file.viewport_capabilities.viewports = [| "Perspective" |])
            "nested repair discarded a supplied viewport list"

    let explicit_disabled = ConfigDocument.to_object ConfigSchema.defaults
    let disabled_list = JsonObject()
    disabled_list["mode"] <- JsonValue.Create(string ViewportNameListMode.DisabledAll)
    disabled_list["viewports"] <- JsonArray()
    set_value explicit_disabled "viewport_capabilities" disabled_list

    match ConfigRepair.repair_document explicit_disabled with
    | Error error -> fail error
    | Ok repaired ->
        require
            (repaired.config_file.viewport_capabilities.mode = ViewportNameListMode.DisabledAll)
            "explicit disabled mode was not preserved"

        require
            (Array.isEmpty repaired.config_file.viewport_capabilities.viewports)
            "explicit empty viewport list was not preserved"

    let malformed_nested = ConfigDocument.to_object ConfigSchema.defaults
    let malformed_list = JsonObject()
    malformed_list["mode"] <- JsonValue.Create "broken"
    malformed_list["viewports"] <- JsonArray(JsonValue.Create "Top")
    set_value malformed_nested "viewport_capabilities" malformed_list

    match ConfigRepair.repair_document malformed_nested with
    | Error error -> fail error
    | Ok repaired ->
        require
            (repaired.config_file.viewport_capabilities.mode = ConfigSchema.defaults.viewport_capabilities.mode)
            "malformed nested mode was not reset"

        require
            (repaired.config_file.viewport_capabilities.viewports = [| "Top" |])
            "malformed nested mode repair discarded a valid list"

    ConfigStorage.initialize temporary_directory

    let config_path = ConfigStorage.path ()

    match ConfigStorage.load () with
    | Error error -> fail error
    | Ok loaded -> require (loaded.config_file.config_version = ConfigSchema.CURRENT_VERSION) "new config version"

    require (File.Exists config_path) "new config was not written"
    require (backup_count temporary_directory = 0) "new config created a backup"

    let invalid_number = ConfigDocument.to_object ConfigSchema.defaults
    set_value invalid_number "base_speed" (JsonValue.Create 42.)
    set_value invalid_number "parallel_mouse_sensitivity" (JsonValue.Create -10.)
    set_value invalid_number "unused_setting" (JsonValue.Create true)
    write_document config_path invalid_number

    match ConfigStorage.load () with
    | Error error -> fail error
    | Ok loaded ->
        require (loaded.config_file.base_speed = 42.) "valid value was not preserved"

        require
            (loaded.config_file.parallel_mouse_sensitivity = ConfigSchema.defaults.parallel_mouse_sensitivity)
            "invalid number was not reset"

    require (backup_count temporary_directory = 1) "first repair backup"

    let first_backup =
        Directory.EnumerateFiles(temporary_directory, "rhinos-can-fly-config.backup-*.json")
        |> Seq.exactlyOne

    require
        (File.ReadAllText(first_backup) = ConfigDocument.content invalid_number)
        "repair backup did not preserve the original file"

    let repaired_document =
        File.ReadAllText config_path
        |> ConfigDocument.parse
        |> function
            | Ok json -> json
            | Error error -> fail error

    require (not (repaired_document.ContainsKey "unused_setting")) "unknown setting was not removed"

    let dependent_values = ConfigDocument.to_object ConfigSchema.defaults
    set_value dependent_values "base_speed" (JsonValue.Create 42.)
    set_value dependent_values "boost_multiplier" (JsonValue.Create 2.2)
    set_value dependent_values "vertical_speed_multiplier" (JsonValue.Create Double.MaxValue)
    set_value dependent_values "forced_perspective_lens_length_on_flight_start_mm" (JsonValue.Create 24.)
    set_value dependent_values "perspective_lens_length_delta_during_flight_mm" (JsonValue.Create -30.)
    write_document config_path dependent_values

    match ConfigStorage.load () with
    | Error error -> fail error
    | Ok loaded ->
        require (loaded.config_file.base_speed = 42.) "derived repair reset a valid base speed"
        require (loaded.config_file.boost_multiplier = 2.2) "derived repair reset a valid boost multiplier"

        require
            (loaded.config_file.vertical_speed_multiplier = ConfigSchema.defaults.vertical_speed_multiplier)
            "overflowing vertical multiplier was not reset"

        require
            (loaded.config_file.forced_perspective_lens_length_on_flight_start_mm = 24.)
            "lens delta repair reset a valid forced lens"

        let actual_lens_delta =
            loaded.config_file.perspective_lens_length_delta_during_flight_mm

        let expected_lens_delta =
            ConfigSchema.defaults.perspective_lens_length_delta_during_flight_mm

        require (actual_lens_delta = expected_lens_delta) "invalid lens delta was not reset"

    let malformed_value = ConfigDocument.to_object ConfigSchema.defaults
    set_value malformed_value "mouse_sensitivity" (JsonValue.Create "broken")
    write_document config_path malformed_value

    match ConfigStorage.load () with
    | Error error -> fail error
    | Ok loaded ->
        require
            (loaded.config_file.mouse_sensitivity = ConfigSchema.defaults.mouse_sensitivity)
            "malformed field was not reset"

    require (backup_count temporary_directory = 2) "second repair backup"

    File.WriteAllText(config_path, "{ broken")

    match ConfigStorage.load () with
    | Error error -> fail error
    | Ok loaded -> require (loaded.config_file = ConfigSchema.defaults) "malformed document was not reset"

    require (backup_count temporary_directory = 2) "backup retention"

    let future = ConfigDocument.to_object ConfigSchema.defaults
    set_value future "config_version" (JsonValue.Create(ConfigSchema.CURRENT_VERSION + 1))
    write_document config_path future
    let future_content = File.ReadAllText config_path

    match ConfigStorage.load () with
    | Ok _ -> fail "future config was accepted"
    | Error _ -> ()

    require (File.ReadAllText(config_path) = future_content) "future config was changed"
    require (backup_count temporary_directory = 2) "future config created a backup"

    let malformed_before_save = "{ broken during save"
    File.WriteAllText(config_path, malformed_before_save)
    File.SetLastWriteTimeUtc(config_path, DateTime.UtcNow.AddYears(-10))

    let requested =
        { ConfigSchema.defaults with
            base_speed = 44. }

    match ConfigStorage.save requested with
    | Error error -> fail $"save after a failed future-version load: {error}"
    | Ok saved -> require (saved.config_file.base_speed = 44.) "saved config value"

    require (backup_count temporary_directory = 2) "save repair backup retention"

    require
        (backup_contents temporary_directory |> List.contains malformed_before_save)
        "save repair pruned the newest backup by the source file timestamp"

    printfn "Config recovery check passed."
finally
    if Directory.Exists temporary_directory then
        Directory.Delete(temporary_directory, true)
