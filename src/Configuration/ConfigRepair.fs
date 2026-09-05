module RhinosCanFly.ConfigRepair

open System
open System.Collections.Generic
open System.Text.Json.Nodes

type RepairResult =
    { config_file: FlyConfigFile
      config: FlyConfig
      document: JsonObject
      changed: bool
      messages: string list }

[<Literal>]
let MAXIMUM_FIELD_REPAIR_PASSES = 8

let compile_defaults () =
    match ConfigCompiler.compile ConfigSchema.defaults with
    | Ok config -> config
    | Error error -> failwith $"The default configuration is invalid:{Environment.NewLine}{error}"

let reset_to_defaults (message: string) =
    { config_file = ConfigSchema.defaults
      config = compile_defaults ()
      document = ConfigDocument.to_object ConfigSchema.defaults
      changed = true
      messages = [ message ] }

let repair_malformed_values (json: JsonObject) (defaults: JsonObject) =
    match ConfigDocument.deserialize json with
    | Ok _ -> []
    | Error _ ->
        let repaired = ResizeArray<string>()

        for property: KeyValuePair<string, JsonNode> in defaults do
            let candidate = defaults.DeepClone().AsObject()
            let source_value = json[property.Key]
            candidate[property.Key] <- ConfigDocument.clone source_value

            match ConfigDocument.deserialize candidate with
            | Ok _ -> ()
            | Error _ ->
                json[property.Key] <- ConfigDocument.clone property.Value
                repaired.Add property.Key

        List.ofSeq repaired

let canonicalize_properties (json: JsonObject) (defaults: JsonObject) =
    let known_names = Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)

    for property in defaults do
        known_names[property.Key] <- property.Key

    let source_names =
        json
        |> Seq.map (fun (property: KeyValuePair<string, JsonNode>) -> property.Key)
        |> List.ofSeq

    let mutable removed = 0
    let mutable renamed = 0

    for name in source_names do
        let mutable canonical_name = ""

        if not (known_names.TryGetValue(name, &canonical_name)) then
            json.Remove name |> ignore
            removed <- removed + 1
        elif not (String.Equals(name, canonical_name, StringComparison.Ordinal)) then
            if not (json.ContainsKey canonical_name) then
                json[canonical_name] <- ConfigDocument.clone json[name]

            json.Remove name |> ignore
            renamed <- renamed + 1

    removed, renamed

let add_missing_properties (json: JsonObject) (defaults: JsonObject) =
    let added = ResizeArray<string>()

    for property in defaults do
        if not (json.ContainsKey property.Key) then
            json[property.Key] <- ConfigDocument.clone property.Value
            added.Add property.Key

    List.ofSeq added

let nested_object_names (defaults: JsonObject) =
    defaults
    |> Seq.choose (fun (property: KeyValuePair<string, JsonNode>) ->
        match property.Value with
        | :? JsonObject -> Some property.Key
        | _ -> None)
    |> List.ofSeq

let nested_children (json: JsonObject) (defaults: JsonObject) =
    nested_object_names defaults
    |> List.choose (fun (name: string) ->
        match json[name], defaults[name] with
        | (:? JsonObject as child), (:? JsonObject as child_defaults) -> Some(name, child, child_defaults)
        | _ -> None)

let repair_nested_members (json: JsonObject) (defaults: JsonObject) =
    let repaired = ResizeArray<string>()

    for name, child, child_defaults in nested_children json defaults do
        let removed, renamed = canonicalize_properties child child_defaults

        if removed > 0 then
            repaired.Add $"{name}: removed {removed} unknown member(s)"

        if renamed > 0 then
            repaired.Add $"{name}: normalized {renamed} member name(s)"

        for added in add_missing_properties child child_defaults do
            repaired.Add $"added missing setting {name}.{added}"

    List.ofSeq repaired

let repair_nested_values (json: JsonObject) (defaults: JsonObject) =
    match ConfigDocument.deserialize json with
    | Ok _ -> []
    | Error _ ->
        let repaired = ResizeArray<string>()

        for name, child, child_defaults in nested_children json defaults do
            let originals =
                child_defaults
                |> Seq.map (fun (property: KeyValuePair<string, JsonNode>) ->
                    property.Key, ConfigDocument.clone child[property.Key])
                |> List.ofSeq

            for member_property: KeyValuePair<string, JsonNode> in child_defaults do
                child[member_property.Key] <- ConfigDocument.clone member_property.Value

            match ConfigDocument.deserialize json with
            | Error _ ->
                for key, original in originals do
                    child[key] <- original
            | Ok _ ->
                for key, original in originals do
                    let defaulted = ConfigDocument.clone child[key]
                    child[key] <- original

                    match ConfigDocument.deserialize json with
                    | Ok _ -> ()
                    | Error _ ->
                        child[key] <- defaulted
                        repaired.Add $"{name}.{key}"

        List.ofSeq repaired

let apply_typed_repairs (source: FlyConfigFile) (issues: ConfigCompiler.ConfigIssue list) =
    issues
    |> List.fold (fun (current: FlyConfigFile) (issue: ConfigCompiler.ConfigIssue) -> issue.repair current) source
    |> ConfigSchema.normalize

let repaired_messages (issues: ConfigCompiler.ConfigIssue list) =
    issues
    |> List.distinctBy (fun (issue: ConfigCompiler.ConfigIssue) -> issue.setting, issue.message)
    |> List.map (fun (issue: ConfigCompiler.ConfigIssue) -> $"reset {issue.setting}: {issue.message}")

let compile_with_repairs (source: FlyConfigFile) (messages: ResizeArray<string>) =
    let rec repair (remaining: int) (current: FlyConfigFile) =
        match ConfigCompiler.compile_detailed current with
        | Ok config -> current, config
        | Error issues ->
            for message in repaired_messages issues do
                messages.Add message

            let repaired = apply_typed_repairs current issues

            if remaining > 0 && repaired <> current then
                repair (remaining - 1) repaired
            else
                messages.Add
                    $"reset settings to defaults after field repair failed: {ConfigCompiler.format_issues issues}"

                ConfigSchema.defaults, compile_defaults ()

    repair MAXIMUM_FIELD_REPAIR_PASSES source

let repair_document (source_json: JsonObject) =
    match ConfigMigration.route source_json with
    | Error error -> Error error
    | Ok routed ->
        let json = source_json
        let before = json.ToJsonString()
        let messages = ResizeArray<string>(routed.messages)
        let defaults = ConfigDocument.to_object ConfigSchema.defaults
        let removed, renamed = canonicalize_properties json defaults

        if removed > 0 then
            messages.Add $"removed {removed} unknown setting(s)"

        if renamed > 0 then
            messages.Add $"normalized {renamed} setting name(s)"

        let added = add_missing_properties json defaults

        if not (List.isEmpty added) then
            messages.Add $"added {List.length added} missing setting(s)"

        let nested_repairs = repair_nested_members json defaults

        for message in nested_repairs do
            messages.Add message

        let nested_malformed = repair_nested_values json defaults

        for name in nested_malformed do
            messages.Add $"reset {name}: malformed value"

        let malformed = repair_malformed_values json defaults

        for name in malformed do
            messages.Add $"reset {name}: malformed value"

        let parsed = ConfigDocument.deserialize json

        let source, config =
            match parsed with
            | Error error ->
                messages.Add $"reset settings to defaults: {error}"
                ConfigSchema.defaults, compile_defaults ()
            | Ok value -> compile_with_repairs (ConfigSchema.normalize value) messages

        let current_source =
            { source with
                config_version = ConfigSchema.CURRENT_VERSION }

        ConfigDocument.merge_known_values json current_source

        let changed =
            routed.version_changed
            || removed > 0
            || renamed > 0
            || not (List.isEmpty added)
            || not (List.isEmpty nested_repairs)
            || not (List.isEmpty nested_malformed)
            || not (List.isEmpty malformed)
            || json.ToJsonString() <> before

        Ok
            { config_file = current_source
              config = config
              document = json
              changed = changed
              messages = messages |> Seq.distinct |> List.ofSeq }
