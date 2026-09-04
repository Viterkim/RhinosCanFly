module RhinosCanFly.ConfigStorage

open System
open System.Globalization
open System.IO
open System.Text

[<Literal>]
let AUTOMATIC_BACKUP_LIMIT = 2

[<Literal>]
let BACKUP_TIMESTAMP_FORMAT = "yyyyMMdd-HHmmss-fff"

let mutable settings_root: string option = None

[<RequireQualifiedAccess>]
type BackupRequirement =
    | NotRequired
    | Required

let initialize (directory: string) =
    Directory.CreateDirectory directory |> ignore
    settings_root <- Some directory

let settings_directory () =
    match settings_root with
    | Some directory -> directory
    | None -> failwith "The RhinosCanFly settings directory has not been initialized."

let path () =
    Path.Combine(settings_directory (), "rhinos-can-fly-config.json")

let with_lock (config_path: string) (action: unit -> 'Value) =
    let lock_path = config_path + ".lock"

    use _save_lock =
        new FileStream(lock_path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None)

    action ()

let write_atomic (config_path: string) (content: string) =
    let directory = Path.GetDirectoryName config_path

    let temporary_path =
        Path.Combine(directory, $".{Path.GetFileName config_path}.{Guid.NewGuid():N}.tmp")

    try
        let bytes = UTF8Encoding(false).GetBytes content

        do
            use stream =
                new FileStream(temporary_path, FileMode.CreateNew, FileAccess.Write, FileShare.None)

            stream.Write(bytes, 0, bytes.Length)
            stream.Flush true

        if File.Exists config_path then
            File.Replace(temporary_path, config_path, null, true)
        else
            File.Move(temporary_path, config_path)
    finally
        if File.Exists temporary_path then
            File.Delete temporary_path

let backup_pattern (config_path: string) =
    $"{Path.GetFileNameWithoutExtension config_path}.backup-*.json"

let rec available_backup_path (directory: string) (base_name: string) (attempt: int) =
    let suffix = if attempt = 0 then "" else $"-{attempt}"
    let candidate = Path.Combine(directory, $"{base_name}{suffix}.json")

    if File.Exists candidate then
        available_backup_path directory base_name (attempt + 1)
    else
        candidate

let create_dated_backup (config_path: string) =
    let directory = Path.GetDirectoryName config_path

    let timestamp =
        DateTimeOffset.Now.ToString(BACKUP_TIMESTAMP_FORMAT, CultureInfo.InvariantCulture)

    let base_name = $"{Path.GetFileNameWithoutExtension config_path}.backup-{timestamp}"

    let backup_path = available_backup_path directory base_name 0
    File.Copy(config_path, backup_path, false)
    backup_path

let prune_automatic_backups (config_path: string) =
    let directory = Path.GetDirectoryName config_path

    Directory.EnumerateFiles(directory, backup_pattern config_path)
    |> Seq.sortWith (fun (left: string) (right: string) ->
        let by_creation =
            compare (File.GetCreationTimeUtc right) (File.GetCreationTimeUtc left)

        if by_creation <> 0 then
            by_creation
        else
            StringComparer.Ordinal.Compare(right, left))
    |> Seq.indexed
    |> Seq.iter (fun (index: int, path: string) ->
        if index >= AUTOMATIC_BACKUP_LIMIT then
            File.Delete path)

let backup_requirement (config_path: string) =
    if not (File.Exists config_path) then
        Ok BackupRequirement.NotRequired
    else
        let content = File.ReadAllText config_path

        match ConfigDocument.parse content with
        | Error _ -> Ok BackupRequirement.Required
        | Ok json ->
            match ConfigRepair.repair_document json with
            | Error error -> Error error
            | Ok repaired ->
                Ok(
                    if repaired.changed then
                        BackupRequirement.Required
                    else
                        BackupRequirement.NotRequired
                )

let load_existing (config_path: string) =
    let content = File.ReadAllText config_path

    match ConfigDocument.parse content with
    | Ok json -> ConfigRepair.repair_document json
    | Error _ -> Ok(ConfigRepair.reset_to_defaults "reset malformed settings to defaults")

let load_locked (config_path: string) =
    let created = not (File.Exists config_path)

    let prepared =
        if created then
            Ok(ConfigRepair.reset_to_defaults $"created config at {config_path}")
        else
            load_existing config_path

    match prepared with
    | Error error -> Error error
    | Ok repaired ->
        let messages = ResizeArray<string>(repaired.messages)

        if repaired.changed then
            if not created then
                let backup_path = create_dated_backup config_path
                messages.Add $"backed up previous config to {backup_path}"

            write_atomic config_path (ConfigDocument.content repaired.document)

            try
                prune_automatic_backups config_path
            with error ->
                messages.Add $"could not prune old config backups: {error.Message}"

        Ok
            { config_file = repaired.config_file
              config = repaired.config
              messages = List.ofSeq messages }

let load () =
    try
        let config_path = path ()
        with_lock config_path (fun () -> load_locked config_path)
    with error ->
        Error error.Message

let save_locked (config_path: string) (source: FlyConfigFile) (config: FlyConfig) =
    match backup_requirement config_path with
    | Error error -> Error error
    | Ok backup_requirement ->
        let config_file =
            { source with
                config_version = ConfigSchema.CURRENT_VERSION }

        let content = config_file |> ConfigDocument.to_object |> ConfigDocument.content

        let existing =
            if File.Exists config_path then
                File.ReadAllText config_path
            else
                ""

        let messages = ResizeArray<string>()

        if backup_requirement = BackupRequirement.Required then
            let backup_path = create_dated_backup config_path
            messages.Add $"backed up previous config to {backup_path}"

        if existing <> content then
            write_atomic config_path content

        if backup_requirement = BackupRequirement.Required then
            try
                prune_automatic_backups config_path
            with error ->
                messages.Add $"could not prune old config backups: {error.Message}"

        Ok
            { config_file = config_file
              config = config
              messages = List.ofSeq messages }

let save (source: FlyConfigFile) =
    let normalized_source = ConfigSchema.normalize source

    match ConfigCompiler.compile normalized_source with
    | Error error -> Error error
    | Ok config ->
        try
            let config_path = path ()
            with_lock config_path (fun () -> save_locked config_path normalized_source config)
        with error ->
            Error error.Message

let read_raw () =
    try
        let config_path = path ()

        with_lock config_path (fun () ->
            let content =
                if File.Exists config_path then
                    File.ReadAllText config_path
                else
                    ""

            Ok(config_path, content))
    with error ->
        Error error.Message
