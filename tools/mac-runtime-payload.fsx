open System
open System.Collections.Generic
open System.IO
open System.Security.Cryptography
open System.Text.Json

type PayloadFile =
    { path: string
      hash: string
      debug: bool }

let hash_stream (stream: Stream) =
    Convert.ToHexString(SHA256.HashData stream)

let hash_file (path: string) =
    use stream = File.OpenRead path
    hash_stream stream

let asset_path (directory: string) (relative: string) =
    let relative = relative.Replace('\\', '/')

    if
        String.IsNullOrWhiteSpace relative
        || relative.Contains ':'
        || relative.Split('/')
           |> Array.exists (fun (part: string) -> part = "" || part = "." || part = "..")
        || Path.IsPathRooted relative
    then
        failwith $"Invalid runtime asset: {relative}."

    let prefix =
        Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar)
        + string Path.DirectorySeparatorChar

    let path = Path.GetFullPath(Path.Combine(directory, relative))

    if not (path.StartsWith(prefix, StringComparison.Ordinal)) then
        failwith $"Runtime asset is outside the build directory: {relative}."

    path

let inventory (output: string) =
    use dependencies =
        JsonDocument.Parse(File.ReadAllText(Path.Combine(output, "RhinosCanFly.deps.json")))

    let target_name =
        dependencies.RootElement.GetProperty("runtimeTarget").GetProperty("name").GetString()

    let target =
        dependencies.RootElement.GetProperty("targets").GetProperty(target_name)

    let files = HashSet<string>(StringComparer.OrdinalIgnoreCase)

    files.UnionWith
        [ "RhinosCanFly.rhp"
          "FSharp.Core.dll"
          "RhinosCanFly.deps.json"
          "libRhinosCanFlyMac.dylib" ]

    for library in target.EnumerateObject() do
        for kind in [ "runtime"; "native"; "runtimeTargets"; "resources" ] do
            let mutable assets = Unchecked.defaultof<JsonElement>

            if library.Value.TryGetProperty(kind, &assets) then
                for asset in assets.EnumerateObject() do
                    asset_path output asset.Name |> ignore
                    let filename = Path.GetFileName(asset.Name.Replace('\\', '/'))

                    if filename <> "_._" then
                        let relative =
                            match kind with
                            | "resources" -> asset.Value.GetProperty("locale").GetString() + "/" + filename
                            | "runtimeTargets" -> asset.Name.Replace('\\', '/')
                            | _ -> filename

                        asset_path output relative |> ignore
                        files.Add relative |> ignore

    for optional in [ "RhinosCanFly.runtimeconfig.json"; "RhinosCanFly.pdb" ] do
        if File.Exists(Path.Combine(output, optional)) then
            files.Add optional |> ignore

    files
    |> Seq.sort
    |> Seq.map (fun (relative: string) ->
        if
            [ "RhinoCommon.dll"; "Rhino.UI.dll"; "Eto.dll"; "Ed.Eto.dll" ]
            |> List.exists (fun (name: string) ->
                String.Equals(Path.GetFileName relative, name, StringComparison.OrdinalIgnoreCase))
        then
            failwith $"Host assembly in runtime dependencies: {relative}."

        { path = relative
          hash = hash_file (asset_path output relative)
          debug = relative = "RhinosCanFly.pdb" })
    |> Seq.toArray

let check_files (directory: string) (files: PayloadFile array) =
    for file in files do
        if hash_file (asset_path directory file.path) <> file.hash then
            failwith $"Runtime payload changed: {file.path}."
