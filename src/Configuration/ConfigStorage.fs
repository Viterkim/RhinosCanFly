module RhinosCanFly.ConfigStorage

open System
open System.Globalization
open System.IO
open System.Text

[<Literal>]
let AUTOMATIC_BACKUP_LIMIT = 2

[<Literal>]
let BACKUP_TIMESTAMP_FORMAT = "yyyyMMdd-HHmmss-fff"

let mutable settingsRoot: string option = None

[<RequireQualifiedAccess>]
type BackupRequirement =
    | NotRequired
    | Required

let initialize (directory: string) =
    Directory.CreateDirectory directory |> ignore
    settingsRoot <- Some directory

let settings_directory () =
    match settingsRoot with
    | Some directory -> directory
    | None -> failwith "The RhinosCanFly settings directory has not been initialized."

let path () =
    Path.Combine(settings_directory (), "rhinos-can-fly-config.json")

let with_lock (configPath: string) (action: unit -> 'Value) =
    let lockPath = configPath + ".lock"

    use _saveLock =
        new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None)

    action ()

let write_atomic (configPath: string) (content: string) =
    let directory = Path.GetDirectoryName configPath

    let temporaryPath =
        Path.Combine(directory, $".{Path.GetFileName configPath}.{Guid.NewGuid():N}.tmp")

    try
        let bytes = UTF8Encoding(false).GetBytes content

        do
            use stream =
                new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None)

            stream.Write(bytes, 0, bytes.Length)
            stream.Flush true

        if File.Exists configPath then
            File.Replace(temporaryPath, configPath, null, true)
        else
            File.Move(temporaryPath, configPath)
    finally
        if File.Exists temporaryPath then
            File.Delete temporaryPath

let backup_pattern (configPath: string) =
    $"{Path.GetFileNameWithoutExtension configPath}.backup-*.json"

let rec available_backup_path (directory: string) (baseName: string) (attempt: int) =
    let suffix = if attempt = 0 then "" else $"-{attempt}"
    let candidate = Path.Combine(directory, $"{baseName}{suffix}.json")

    if File.Exists candidate then
        available_backup_path directory baseName (attempt + 1)
    else
        candidate

let create_dated_backup (configPath: string) =
    let directory = Path.GetDirectoryName configPath

    let timestamp =
        DateTimeOffset.Now.ToString(BACKUP_TIMESTAMP_FORMAT, CultureInfo.InvariantCulture)

    let baseName = $"{Path.GetFileNameWithoutExtension configPath}.backup-{timestamp}"

    let backupPath = available_backup_path directory baseName 0
    File.Copy(configPath, backupPath, false)
    backupPath

let prune_automatic_backups (configPath: string) =
    let directory = Path.GetDirectoryName configPath

    Directory.EnumerateFiles(directory, backup_pattern configPath)
    |> Seq.sortWith (fun (left: string) (right: string) ->
        let byCreation =
            compare (File.GetCreationTimeUtc right) (File.GetCreationTimeUtc left)

        if byCreation <> 0 then
            byCreation
        else
            StringComparer.Ordinal.Compare(right, left))
    |> Seq.skip AUTOMATIC_BACKUP_LIMIT
    |> Seq.iter File.Delete

let backup_requirement (configPath: string) =
    if not (File.Exists configPath) then
        Ok BackupRequirement.NotRequired
    else
        let content = File.ReadAllText configPath

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

let load_existing (configPath: string) =
    let content = File.ReadAllText configPath

    match ConfigDocument.parse content with
    | Ok json -> ConfigRepair.repair_document json
    | Error _ -> Ok(ConfigRepair.reset_to_defaults "reset malformed settings to defaults")

let load_locked (configPath: string) =
    let created = not (File.Exists configPath)

    let prepared =
        if created then
            Ok(ConfigRepair.reset_to_defaults $"created config at {configPath}")
        else
            load_existing configPath

    match prepared with
    | Error error -> Error error
    | Ok repaired ->
        let messages = ResizeArray<string>(repaired.messages)

        if repaired.changed then
            if not created then
                let backupPath = create_dated_backup configPath
                messages.Add $"backed up previous config to {backupPath}"

            write_atomic configPath (ConfigDocument.content repaired.document)

            try
                prune_automatic_backups configPath
            with error ->
                messages.Add $"could not prune old config backups: {error.Message}"

        Ok
            { config_file = repaired.config_file
              config = repaired.config
              messages = List.ofSeq messages }

let load () =
    try
        let configPath = path ()
        with_lock configPath (fun () -> load_locked configPath)
    with error ->
        Error error.Message

let save_locked (configPath: string) (source: FlyConfigFile) (config: FlyConfig) =
    match backup_requirement configPath with
    | Error error -> Error error
    | Ok backupRequirement ->
        let configFile =
            { source with
                config_version = ConfigSchema.CURRENT_VERSION }

        let content = configFile |> ConfigDocument.to_object |> ConfigDocument.content

        let existing =
            if File.Exists configPath then
                File.ReadAllText configPath
            else
                ""

        let messages = ResizeArray<string>()

        if backupRequirement = BackupRequirement.Required then
            let backupPath = create_dated_backup configPath
            messages.Add $"backed up previous config to {backupPath}"

        if existing <> content then
            write_atomic configPath content

        if backupRequirement = BackupRequirement.Required then
            try
                prune_automatic_backups configPath
            with error ->
                messages.Add $"could not prune old config backups: {error.Message}"

        Ok
            { config_file = configFile
              config = config
              messages = List.ofSeq messages }

let save (source: FlyConfigFile) =
    let normalizedSource = ConfigSchema.normalize source

    match ConfigCompiler.compile normalizedSource with
    | Error error -> Error error
    | Ok config ->
        try
            let configPath = path ()
            with_lock configPath (fun () -> save_locked configPath normalizedSource config)
        with error ->
            Error error.Message

let read_raw () =
    try
        let configPath = path ()

        with_lock configPath (fun () ->
            let content =
                if File.Exists configPath then
                    File.ReadAllText configPath
                else
                    ""

            Ok(configPath, content))
    with error ->
        Error error.Message
