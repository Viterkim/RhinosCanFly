#load "mac-runtime-payload.fsx"
#load "check-plugin-metadata.fsx"

open ``Mac-runtime-payload``
open ``Check-plugin-metadata``
open System
open System.Collections.Generic
open System.IO
open System.IO.Compression
open System.Text.RegularExpressions

let manifest_value (content: string) (key: string) =
    let values =
        Regex.Matches(content, $"^{key}:[ \\t]*['\"]?([^'\"\\s]+)['\"]?[ \\t]*\\r?$", RegexOptions.Multiline)

    if values.Count <> 1 then
        failwith $"Expected one manifest {key}."

    values[0].Groups[1].Value

let archive_entries (archive: ZipArchive) =
    let entries = Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase)

    for entry in archive.Entries do
        if not (entry.FullName.EndsWith('/')) then
            asset_path (Path.GetTempPath()) entry.FullName |> ignore

            if not (entries.TryAdd(entry.FullName, entry)) then
                failwith $"Duplicate archive file: {entry.FullName}."

    entries

let read_entry (entry: ZipArchiveEntry) =
    use reader = new StreamReader(entry.Open())
    reader.ReadToEnd()

let check_manifest (expected: string) (actual: string) =
    for key in [ "name"; "version" ] do
        if manifest_value expected key <> manifest_value actual key then
            failwith $"Packaged manifest {key} does not match staging."

let check_archive (stage: string) (path: string) =
    let expected =
        inventory stage
        |> Array.map (fun (file: PayloadFile) -> file.path, file.hash)
        |> dict

    let files = Dictionary<string, string>(expected, StringComparer.OrdinalIgnoreCase)

    for name in [ "manifest.yml"; "icon.png"; "README.md"; "LICENSE" ] do
        files.Add(name, hash_file (Path.Combine(stage, name)))

    use archive = ZipFile.OpenRead path
    let entries = archive_entries archive

    if
        files.Count <> entries.Count
        || entries.Keys |> Seq.exists (fun (name: string) -> not (files.ContainsKey name))
    then
        failwith "The Mac archive file set does not match the staged payload."

    for KeyValue(name, hash) in files do
        let entry = entries[name]

        if name = "manifest.yml" then
            check_manifest (File.ReadAllText(Path.Combine(stage, name))) (read_entry entry)
        else
            use stream = entry.Open()

            if hash_stream stream <> hash then
                failwith $"Mac archive content changed: {name}."

let check_install (path: string) (directory: string) =
    use archive = ZipFile.OpenRead path
    let entries = archive_entries archive

    if
        not (
            entries.ContainsKey "RhinosCanFly.rhp"
            && entries.ContainsKey "libRhinosCanFlyMac.dylib"
        )
    then
        failwith "Recovery archive has no Mac plugin/bridge."

    // Yak owns manifest.txt beside the version folders. Compare the version's files.
    let actual =
        Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
        |> Seq.map (fun (file: string) -> Path.GetRelativePath(directory, file).Replace('\\', '/'))
        |> Set.ofSeq

    if actual <> (entries.Keys |> Set.ofSeq) then
        failwith "Installed file set does not match the recovery archive."

    for KeyValue(name, entry) in entries do
        use stream = entry.Open()

        if hash_stream stream <> hash_file (asset_path directory name) then
            failwith $"Installed content differs: {name}."

let check_recovery (path: string) =
    use archive = ZipFile.OpenRead path
    archive_entries archive |> ignore

    let temporary =
        Path.Combine(Path.GetTempPath(), "rcf-recovery-" + Guid.NewGuid().ToString("N"))

    try
        ZipFile.ExtractToDirectory(path, temporary)
        check_archive temporary path
    finally
        if Directory.Exists temporary then
            Directory.Delete(temporary, true)

let check_release
    (root: string)
    (version: string)
    (framework: string)
    (rhino_package: string)
    (zip: string)
    (package: string)
    =
    let rhino_version = rhino_package.Split('-')[0]
    let parts = rhino_version.Split('.')
    let distribution = $"rh{parts[0]}_{parts[1]}-mac"
    let expected_package = $"rhinoscanfly-{version}-{distribution}.yak"

    if Path.GetFileName package <> expected_package then
        failwith $"Expected Mac Yak filename {expected_package}."

    let temporary =
        Path.Combine(Path.GetTempPath(), "rcf-release-" + Guid.NewGuid().ToString("N"))

    try
        use archive = ZipFile.OpenRead zip
        archive_entries archive |> ignore
        ZipFile.ExtractToDirectory(zip, temporary)
        check_archive temporary zip
        check_archive temporary package

        use yak = ZipFile.OpenRead package

        if manifest_value (read_entry (yak.GetEntry "manifest.yml")) "platform" <> "mac" then
            failwith "Yak manifest does not target Mac."

        let manifest = File.ReadAllText(Path.Combine(temporary, "manifest.yml"))

        if
            manifest_value manifest "name" <> "RhinosCanFly"
            || manifest_value manifest "version" <> version
        then
            failwith "Mac manifest does not match this release."

        let assembly_info = File.ReadAllText(Path.Combine(root, "src", "AssemblyInfo.fs"))
        let guid = Regex.Match(assembly_info, "assembly: Guid\\(\"([^\"]+)\"\\)")

        if not guid.Success then
            failwith "Missing plug-in GUID in AssemblyInfo.fs."

        check_plugin
            (Path.Combine(temporary, "RhinosCanFly.rhp"))
            "RhinosCanFly"
            version
            ($".NETCoreApp,Version=v{framework.Substring(3)}")
            rhino_version
            guid.Groups[1].Value
    finally
        if Directory.Exists temporary then
            Directory.Delete(temporary, true)

match fsi.CommandLineArgs |> Array.skip 1 with
| [| "archive"; stage; path |] ->
    check_archive stage path
    printfn "Mac package file set and hashes match staging."
| [| "installed"; path; directory |] ->
    check_install path directory
    printfn "Installed Mac package matches its archive."
| [| "recovery"; path |] ->
    check_recovery path
    printfn "Recovery archive contains the complete Mac payload."
| [| "release"; root; version; framework; rhino_package; zip; package |] ->
    check_release root version framework rhino_package zip package
    printfn "Mac release file sets, hashes and plugin metadata passed."
| _ ->
    failwith
        "Expected archive stage package, installed package version-directory, recovery package, or release root version framework RhinoCommon ZIP Yak."
