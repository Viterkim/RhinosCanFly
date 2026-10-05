#load "check-plugin-metadata.fsx"
#load "mac-runtime-payload.fsx"

open ``Check-plugin-metadata``
open ``Mac-runtime-payload``
open System
open System.IO
open System.Text.RegularExpressions

let single_value (content: string) (pattern: string) =
    let matches = Regex.Matches(content, pattern, RegexOptions.Multiline)

    if matches.Count <> 1 then
        failwith $"Expected one declaration matching {pattern}."

    matches[0].Groups[1].Value

let stage_package (root: string) (output: string) (framework: string) (rhino_package: string) (stage: string) =
    let manifest = File.ReadAllText(Path.Combine(root, "manifest.yml"))
    let name = single_value manifest @"^name:[ \t]*([A-Za-z0-9]+)[ \t]*\r?$"
    let version = single_value manifest @"^version:[ \t]*(\d+\.\d+\.\d+)[ \t]*\r?$"
    let assembly_info = File.ReadAllText(Path.Combine(root, "src", "AssemblyInfo.fs"))

    if name <> "RhinosCanFly" then
        failwith "Unexpected manifest name."

    for attribute, expected in
        [ "AssemblyInformationalVersion", version
          "AssemblyVersion", version + ".0"
          "AssemblyFileVersion", version + ".0" ] do
        if
            single_value assembly_info ($"assembly: {attribute}" + @"\(""([^""]+)""\)")
            <> expected
        then
            failwith $"{attribute} does not match manifest version {version}."

    let guid = single_value assembly_info @"assembly: Guid\(""([^""]+)""\)"
    let framework_version = single_value framework @"^net(\d+\.\d+)$"

    check_plugin
        (Path.Combine(output, "RhinosCanFly.rhp"))
        name
        version
        ($".NETCoreApp,Version=v{framework_version}")
        (rhino_package.Split('-')[0])
        guid

    let payload = inventory output

    for file in payload do
        let source = asset_path output file.path
        let destination = asset_path stage file.path
        Directory.CreateDirectory(Path.GetDirectoryName destination) |> ignore
        File.Copy(source, destination, false)

    check_files stage payload

    for supporting in [ "manifest.yml"; "icon.png"; "README.md"; "LICENSE" ] do
        File.Copy(Path.Combine(root, supporting), Path.Combine(stage, supporting), false)

match fsi.CommandLineArgs |> Array.skip 1 with
| [| root; output; framework; rhino_package; stage |] -> stage_package root output framework rhino_package stage
| _ -> failwith "Expected repository, build output, target framework, RhinoCommon package version and stage directory."
