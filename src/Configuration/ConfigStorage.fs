module RhinosCanFly.ConfigStorage

open System
open System.IO
open System.Text.Json
open System.Text.Json.Nodes

let options =
    JsonSerializerOptions(
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        WriteIndented = true
    )

let mutable settingsRoot: string option = None

let initialize (directory: string) =
    Directory.CreateDirectory directory |> ignore
    settingsRoot <- Some directory

let path () =
    match settingsRoot with
    | Some directory -> Path.Combine(directory, "rhinos-can-fly-config.json")
    | None -> failwith "The RhinosCanFly settings directory has not been initialized."

let to_object (value: FlyConfigFile) =
    JsonSerializer.SerializeToNode(ConfigSchema.normalize_numbers value, options).AsObject()

let write_json (configPath: string) (json: JsonObject) =
    File.WriteAllText(configPath, json.ToJsonString options + Environment.NewLine)

let merge_known_values (target: JsonObject) (source: FlyConfigFile) =
    for property in to_object source do
        target[property.Key] <- property.Value.DeepClone()

let load () =
    try
        let configPath = path ()
        let created = not (File.Exists configPath)

        let json =
            if created then
                to_object ConfigSchema.defaults
            else
                match JsonNode.Parse(File.ReadAllText configPath) with
                | :? JsonObject as value -> value
                | _ -> failwith $"The config root must be a JSON object: {configPath}"

        let messages = ResizeArray<string>()
        let mutable changed = created
        let beforeNormalization = json.ToJsonString()

        if created then
            messages.Add $"created config at {configPath}"

        let defaults = to_object ConfigSchema.defaults

        let knownNames =
            defaults
            |> Seq.map (fun (property: Collections.Generic.KeyValuePair<string, JsonNode>) -> property.Key)
            |> Set.ofSeq

        let unknownNames =
            json
            |> Seq.map (fun (property: Collections.Generic.KeyValuePair<string, JsonNode>) -> property.Key)
            |> Seq.filter (fun (name: string) -> not (knownNames.Contains name))
            |> List.ofSeq

        for name in unknownNames do
            json.Remove name |> ignore
            changed <- true

        if not (List.isEmpty unknownNames) then
            messages.Add $"removed {unknownNames.Length} unknown setting(s)"

        json["config_version"] <- JsonValue.Create ConfigSchema.current_version
        let mutable added = 0

        for property in defaults do
            if not (json.ContainsKey property.Key) then
                json[property.Key] <- property.Value.DeepClone()
                added <- added + 1
                changed <- true

        if json.ToJsonString() <> beforeNormalization then
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
            write_json configPath json

        Ok
            { config_file = source
              config = config
              messages = List.ofSeq messages }
    with error ->
        Error error.Message

let save (source: FlyConfigFile) =
    let normalizedSource = ConfigSchema.normalize_numbers source

    match ConfigSchema.compile normalizedSource with
    | Error error -> Error error
    | Ok config ->
        try
            let configPath = path ()

            let configFile =
                { normalizedSource with
                    config_version = ConfigSchema.current_version }

            let json = to_object configFile
            let content = json.ToJsonString options + Environment.NewLine

            let existing =
                if File.Exists configPath then
                    File.ReadAllText configPath
                else
                    ""

            if existing <> content then
                File.WriteAllText(configPath, content)

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
