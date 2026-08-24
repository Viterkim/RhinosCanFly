module RhinosCanFly.ConfigMigration

open System.Text.Json.Nodes

type RouteResult =
    { version_changed: bool
      messages: string list }

let future_version_error (version: int) =
    $"The config version {version} is newer than this RhinosCanFly build supports ({ConfigSchema.CURRENT_VERSION}). The file was left unchanged."

let route (json: JsonObject) =
    let sourceVersion = ConfigDocument.config_version json

    match sourceVersion with
    | Some version when version > ConfigSchema.CURRENT_VERSION -> Error(future_version_error version)
    | Some version when version = ConfigSchema.CURRENT_VERSION ->
        Ok
            { version_changed = false
              messages = [] }
    | Some version ->
        Ok
            { version_changed = true
              messages = [ $"updated config version from {version} to {ConfigSchema.CURRENT_VERSION}" ] }
    | None ->
        Ok
            { version_changed = true
              messages = [ $"added config version {ConfigSchema.CURRENT_VERSION}" ] }
