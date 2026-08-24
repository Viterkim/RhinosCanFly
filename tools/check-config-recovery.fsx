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

let write_document (configPath: string) (json: JsonObject) =
    File.WriteAllText(configPath, ConfigDocument.content json)

let set_value (json: JsonObject) (name: string) (value: JsonNode) = json[name] <- value

let temporaryDirectory =
    Path.Combine(Path.GetTempPath(), $"rhinos-can-fly-config-check-{Guid.NewGuid():N}")

try
    let previousVersion = ConfigDocument.to_object ConfigSchema.defaults
    set_value previousVersion "config_version" (JsonValue.Create(ConfigSchema.CURRENT_VERSION - 1))
    set_value previousVersion "base_speed" (JsonValue.Create 43.)
    previousVersion.Remove "right_click_enters_parallel_views" |> ignore
    set_value previousVersion "removed_setting" (JsonValue.Create true)

    match ConfigRepair.repair_document previousVersion with
    | Error error -> fail error
    | Ok repaired ->
        require repaired.changed "previous config version was not migrated"
        require (repaired.config_file.config_version = ConfigSchema.CURRENT_VERSION) "migrated config version"
        require (repaired.config_file.base_speed = 43.) "migration did not preserve a known setting"

        require
            (repaired.config_file.right_click_enters_parallel_views = ConfigSchema.defaults.right_click_enters_parallel_views)
            "migration did not add a missing setting with its default"

        require (not (repaired.document.ContainsKey "removed_setting")) "migration kept an unknown setting"

    ConfigStorage.initialize temporaryDirectory

    let configPath = ConfigStorage.path ()

    match ConfigStorage.load () with
    | Error error -> fail error
    | Ok loaded -> require (loaded.config_file.config_version = ConfigSchema.CURRENT_VERSION) "new config version"

    require (File.Exists configPath) "new config was not written"
    require (backup_count temporaryDirectory = 0) "new config created a backup"

    let invalidNumber = ConfigDocument.to_object ConfigSchema.defaults
    set_value invalidNumber "base_speed" (JsonValue.Create 42.)
    set_value invalidNumber "parallel_mouse_sensitivity" (JsonValue.Create -10.)
    set_value invalidNumber "unused_setting" (JsonValue.Create true)
    write_document configPath invalidNumber

    match ConfigStorage.load () with
    | Error error -> fail error
    | Ok loaded ->
        require (loaded.config_file.base_speed = 42.) "valid value was not preserved"

        require
            (loaded.config_file.parallel_mouse_sensitivity = ConfigSchema.defaults.parallel_mouse_sensitivity)
            "invalid number was not reset"

    require (backup_count temporaryDirectory = 1) "first repair backup"

    let firstBackup =
        Directory.EnumerateFiles(temporaryDirectory, "rhinos-can-fly-config.backup-*.json")
        |> Seq.exactlyOne

    require
        (File.ReadAllText(firstBackup) = ConfigDocument.content invalidNumber)
        "repair backup did not preserve the original file"

    let repairedDocument =
        File.ReadAllText configPath
        |> ConfigDocument.parse
        |> function
            | Ok json -> json
            | Error error -> fail error

    require (not (repairedDocument.ContainsKey "unused_setting")) "unknown setting was not removed"

    let dependentValues = ConfigDocument.to_object ConfigSchema.defaults
    set_value dependentValues "base_speed" (JsonValue.Create 42.)
    set_value dependentValues "boost_multiplier" (JsonValue.Create 2.2)
    set_value dependentValues "vertical_speed_multiplier" (JsonValue.Create Double.MaxValue)
    set_value dependentValues "forced_perspective_lens_length_on_flight_start_mm" (JsonValue.Create 24.)
    set_value dependentValues "perspective_lens_length_delta_during_flight_mm" (JsonValue.Create -30.)
    write_document configPath dependentValues

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

        let actualLensDelta =
            loaded.config_file.perspective_lens_length_delta_during_flight_mm

        let expectedLensDelta =
            ConfigSchema.defaults.perspective_lens_length_delta_during_flight_mm

        require (actualLensDelta = expectedLensDelta) "invalid lens delta was not reset"

    let malformedValue = ConfigDocument.to_object ConfigSchema.defaults
    set_value malformedValue "mouse_sensitivity" (JsonValue.Create "broken")
    write_document configPath malformedValue

    match ConfigStorage.load () with
    | Error error -> fail error
    | Ok loaded ->
        require
            (loaded.config_file.mouse_sensitivity = ConfigSchema.defaults.mouse_sensitivity)
            "malformed field was not reset"

    require (backup_count temporaryDirectory = 2) "second repair backup"

    File.WriteAllText(configPath, "{ broken")

    match ConfigStorage.load () with
    | Error error -> fail error
    | Ok loaded -> require (loaded.config_file = ConfigSchema.defaults) "malformed document was not reset"

    require (backup_count temporaryDirectory = 2) "backup retention"

    let future = ConfigDocument.to_object ConfigSchema.defaults
    set_value future "config_version" (JsonValue.Create(ConfigSchema.CURRENT_VERSION + 1))
    write_document configPath future
    let futureContent = File.ReadAllText configPath

    match ConfigStorage.load () with
    | Ok _ -> fail "future config was accepted"
    | Error _ -> ()

    require (File.ReadAllText(configPath) = futureContent) "future config was changed"
    require (backup_count temporaryDirectory = 2) "future config created a backup"

    let malformedBeforeSave = "{ broken during save"
    File.WriteAllText(configPath, malformedBeforeSave)
    File.SetLastWriteTimeUtc(configPath, DateTime.UtcNow.AddYears(-10))

    let requested =
        { ConfigSchema.defaults with
            base_speed = 44. }

    match ConfigStorage.save requested with
    | Error error -> fail $"save after a failed future-version load: {error}"
    | Ok saved -> require (saved.config_file.base_speed = 44.) "saved config value"

    require (backup_count temporaryDirectory = 2) "save repair backup retention"

    require
        (backup_contents temporaryDirectory |> List.contains malformedBeforeSave)
        "save repair pruned the newest backup by the source file timestamp"

    printfn "Config recovery check passed."
finally
    if Directory.Exists temporaryDirectory then
        Directory.Delete(temporaryDirectory, true)
