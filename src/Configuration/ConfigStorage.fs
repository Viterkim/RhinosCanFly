module RhinosCanFly.ConfigStorage

open System
open System.IO
open System.Text
open System.Text.Json
open System.Text.Json.Nodes
open System.Text.Json.Serialization

let options =
    let value =
        JsonSerializerOptions(
            PropertyNameCaseInsensitive = true,
            AllowTrailingCommas = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            WriteIndented = true
        )

    value.Converters.Add(new JsonStringEnumConverter(null, false))
    value

let mutable settingsRoot: string option = None
let mutable saveBlocked: string option = None

let initialize (directory: string) =
    Directory.CreateDirectory directory |> ignore
    settingsRoot <- Some directory
    saveBlocked <- None

let settings_directory () =
    match settingsRoot with
    | Some directory -> directory
    | None -> failwith "The RhinosCanFly settings directory has not been initialized."

let path () =
    Path.Combine(settings_directory (), "rhinos-can-fly-config.json")

let input_diagnostics_path () =
    Path.Combine(settings_directory (), "rhinos-can-fly-input-diagnostics.txt")

let to_object (value: FlyConfigFile) =
    JsonSerializer.SerializeToNode(ConfigSchema.normalize_numbers value, options).AsObject()

let json_content (json: JsonObject) =
    json.ToJsonString options + Environment.NewLine

let documentOptions =
    JsonDocumentOptions(AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip)

let current_file_version (configPath: string) =
    if not (File.Exists configPath) then
        None
    else
        try
            let content = File.ReadAllText configPath

            match JsonNode.Parse(content, Nullable<JsonNodeOptions>(), documentOptions) with
            | :? JsonObject as json ->
                match json["config_version"] with
                | null -> None
                | value -> Some(value.GetValue<int>())
            | _ -> None
        with _ ->
            None

let future_version_error (version: int) =
    $"The config version {version} is newer than this RhinosCanFly build supports ({ConfigSchema.current_version}). The file was left unchanged."

let write_atomic (configPath: string) (content: string) =
    let directory = Path.GetDirectoryName configPath

    let temporaryPath =
        Path.Combine(directory, $".{Path.GetFileName configPath}.{Guid.NewGuid():N}.tmp")

    let backupPath = configPath + ".bak"

    try
        let bytes = UTF8Encoding(false).GetBytes content

        do
            use stream =
                new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None)

            stream.Write(bytes, 0, bytes.Length)
            stream.Flush true

        if File.Exists configPath then
            File.Replace(temporaryPath, configPath, backupPath, true)
        else
            File.Move(temporaryPath, configPath)
    finally
        if File.Exists temporaryPath then
            File.Delete temporaryPath

let merge_known_values (target: JsonObject) (source: FlyConfigFile) =
    for property in to_object source do
        target[property.Key] <-
            if isNull property.Value then
                null
            else
                property.Value.DeepClone()

let load () =
    try
        let configPath = path ()
        let created = not (File.Exists configPath)
        let messages = ResizeArray<string>()
        let mutable malformed = false

        let json =
            if created then
                to_object ConfigSchema.defaults
            else
                let content = File.ReadAllText configPath

                try
                    match JsonNode.Parse(content, Nullable<JsonNodeOptions>(), documentOptions) with
                    | :? JsonObject as value -> value
                    | _ ->
                        malformed <- true
                        to_object ConfigSchema.defaults
                with :? JsonException ->
                    malformed <- true
                    to_object ConfigSchema.defaults

        let futureVersion =
            match json["config_version"] with
            | null -> None
            | versionNode ->
                try
                    Some(versionNode.GetValue<int>())
                with _ ->
                    None

        match futureVersion with
        | Some version when version > ConfigSchema.current_version ->
            let error = future_version_error version

            saveBlocked <- Some error
            failwith error
        | Some _
        | None -> ()

        let mutable changed = created || malformed
        let beforeRepair = json.ToJsonString()

        if created then
            messages.Add $"created config at {configPath}"
        elif malformed then
            messages.Add "reset malformed settings to defaults"

        let defaults = to_object ConfigSchema.defaults

        let knownNames =
            let names =
                Collections.Generic.Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)

            for property in defaults do
                names[property.Key] <- property.Key

            names

        let sourceNames =
            json
            |> Seq.map (fun (property: Collections.Generic.KeyValuePair<string, JsonNode>) -> property.Key)
            |> List.ofSeq

        let mutable removed = 0
        let mutable renamed = 0

        for name in sourceNames do
            let mutable canonicalName = ""

            if not (knownNames.TryGetValue(name, &canonicalName)) then
                json.Remove name |> ignore
                removed <- removed + 1
                changed <- true
            elif not (String.Equals(name, canonicalName, StringComparison.Ordinal)) then
                if not (json.ContainsKey canonicalName) then
                    let value = json[name]
                    json[canonicalName] <- if isNull value then null else value.DeepClone()

                json.Remove name |> ignore
                renamed <- renamed + 1
                changed <- true

        if removed > 0 then
            messages.Add $"removed {removed} unknown setting(s)"

        if renamed > 0 then
            messages.Add $"normalized {renamed} setting name(s)"

        json["config_version"] <- JsonValue.Create ConfigSchema.current_version
        let mutable added = 0

        for property in defaults do
            if not (json.ContainsKey property.Key) then
                json[property.Key] <- property.Value.DeepClone()
                added <- added + 1
                changed <- true

        if json.ToJsonString() <> beforeRepair then
            changed <- true

        if added > 0 then
            messages.Add $"added {added} missing setting(s)"

        let parsed =
            try
                let value = JsonSerializer.Deserialize<FlyConfigFile>(json.ToJsonString(), options)

                if isNull (box value) then
                    Error "the config is empty"
                else
                    Ok value
            with error ->
                Error error.Message

        let source, config =
            match parsed with
            | Ok source ->
                let source = ConfigSchema.normalize_numbers source

                match ConfigSchema.compile source with
                | Ok config -> source, config
                | Error _ ->
                    json.Clear()
                    merge_known_values json ConfigSchema.defaults
                    changed <- true
                    messages.Add "reset invalid settings to defaults"

                    match ConfigSchema.compile ConfigSchema.defaults with
                    | Ok config -> ConfigSchema.defaults, config
                    | Error error -> failwith error
            | Error _ ->
                json.Clear()
                merge_known_values json ConfigSchema.defaults
                changed <- true
                messages.Add "reset malformed settings to defaults"

                match ConfigSchema.compile ConfigSchema.defaults with
                | Ok config -> ConfigSchema.defaults, config
                | Error error -> failwith error

        let beforeNumberNormalization = json.ToJsonString()
        merge_known_values json source

        if json.ToJsonString() <> beforeNumberNormalization then
            changed <- true

        if changed then
            let lockPath = configPath + ".lock"

            use _saveLock =
                new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None)

            match current_file_version configPath with
            | Some version when version > ConfigSchema.current_version ->
                let error = future_version_error version
                saveBlocked <- Some error
                failwith error
            | Some _
            | None -> write_atomic configPath (json_content json)

        saveBlocked <- None

        Ok
            { config_file = source
              config = config
              messages = List.ofSeq messages }
    with error ->
        Error error.Message

let save (source: FlyConfigFile) =
    match saveBlocked with
    | Some error -> Error error
    | None ->
        let normalizedSource = ConfigSchema.normalize_numbers source

        match ConfigSchema.compile normalizedSource with
        | Error error -> Error error
        | Ok config ->
            try
                let configPath = path ()
                let lockPath = configPath + ".lock"

                use _saveLock =
                    new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None)

                match current_file_version configPath with
                | Some version when version > ConfigSchema.current_version ->
                    let error = future_version_error version
                    saveBlocked <- Some error
                    failwith error
                | Some _
                | None -> saveBlocked <- None

                let configFile =
                    { normalizedSource with
                        config_version = ConfigSchema.current_version }

                let json = to_object configFile
                let content = json_content json

                let existing =
                    if File.Exists configPath then
                        File.ReadAllText configPath
                    else
                        ""

                if existing <> content then
                    write_atomic configPath content

                Ok
                    { config_file = configFile
                      config = config
                      messages = [] }
            with error ->
                Error error.Message

let read_raw () =
    try
        let configPath = path ()

        let content =
            if File.Exists configPath then
                File.ReadAllText configPath
            else
                ""

        Ok(configPath, content)
    with error ->
        Error error.Message
