module RhinosCanFly.ConfigDocument

open System
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

let document_options =
    JsonDocumentOptions(AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip)

let clone (value: JsonNode) =
    if isNull value then null else value.DeepClone()

let to_object (value: FlyConfigFile) =
    JsonSerializer.SerializeToNode(ConfigSchema.normalize value, options).AsObject()

let content (json: JsonObject) =
    json.ToJsonString options + Environment.NewLine

let parse (source: string) =
    try
        use document = JsonDocument.Parse(source, document_options)
        let root = document.RootElement

        if root.ValueKind <> JsonValueKind.Object then
            Error "the config root is not an object"
        else
            let mutable future_version = 0

            for property in root.EnumerateObject() do
                if String.Equals(property.Name, "config_version", StringComparison.OrdinalIgnoreCase) then
                    let mutable version = 0

                    if
                        property.Value.ValueKind = JsonValueKind.Number
                        && property.Value.TryGetInt32(&version)
                    then
                        future_version <- max future_version version

            if future_version > ConfigSchema.CURRENT_VERSION then
                let protected_document = JsonObject()
                protected_document["config_version"] <- JsonValue.Create future_version
                Ok protected_document
            else
                let rec check_properties (element: JsonElement) =
                    match element.ValueKind with
                    | JsonValueKind.Object ->
                        let names = Collections.Generic.HashSet<string>(StringComparer.OrdinalIgnoreCase)

                        for property in element.EnumerateObject() do
                            if not (names.Add property.Name) then
                                raise (JsonException $"Duplicate config property: {property.Name}")

                            check_properties property.Value
                    | JsonValueKind.Array ->
                        for item in element.EnumerateArray() do
                            check_properties item
                    | _ -> ()

                check_properties root
                Ok(JsonNode.Parse(source, Nullable<JsonNodeOptions>(), document_options).AsObject())
    with error ->
        Error error.Message

let config_version (json: JsonObject) =
    let mutable maximum = None

    for property: Collections.Generic.KeyValuePair<string, JsonNode> in json do
        if
            String.Equals(property.Key, "config_version", StringComparison.OrdinalIgnoreCase)
            && not (isNull property.Value)
        then
            try
                let version = property.Value.GetValue<int>()

                maximum <-
                    match maximum with
                    | Some current -> Some(max current version)
                    | None -> Some version
            with _ ->
                ()

    maximum

let deserialize (json: JsonObject) =
    try
        let value = JsonSerializer.Deserialize<FlyConfigFile>(json.ToJsonString(), options)

        if isNull (box value) then
            Error "the config is empty"
        else
            Ok value
    with error ->
        Error error.Message

let merge_known_values (target: JsonObject) (source: FlyConfigFile) =
    for property in to_object source do
        target[property.Key] <- clone property.Value
