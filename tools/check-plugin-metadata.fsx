open System
open System.Collections.Generic
open System.IO
open System.Reflection.Metadata
open System.Reflection.PortableExecutable

let check_plugin (path: string) (name: string) (version: string) (framework: string) (rhino: string) (guid: string) =
    use stream = File.OpenRead path
    use pe = new PEReader(stream)
    let reader = pe.GetMetadataReader()
    let assembly = reader.GetAssemblyDefinition()

    if reader.GetString assembly.Name <> name then
        failwith $"The packaged assembly is not {name}."

    let attributes = Dictionary<string, string>()

    for handle in assembly.GetCustomAttributes() do
        let attribute = reader.GetCustomAttribute handle

        if attribute.Constructor.Kind = HandleKind.MemberReference then
            let member_reference =
                reader.GetMemberReference(MemberReferenceHandle.op_Explicit attribute.Constructor)

            if member_reference.Parent.Kind = HandleKind.TypeReference then
                let attribute_type =
                    reader.GetTypeReference(TypeReferenceHandle.op_Explicit member_reference.Parent)

                let attribute_name = reader.GetString attribute_type.Name

                match attribute_name with
                | "AssemblyFileVersionAttribute"
                | "AssemblyInformationalVersionAttribute"
                | "TargetFrameworkAttribute"
                | "GuidAttribute" ->
                    let mutable blob = reader.GetBlobReader attribute.Value

                    if blob.ReadUInt16() <> 1us then
                        failwith $"Invalid plugin attribute '{attribute_name}'."

                    if not (attributes.TryAdd(attribute_name, blob.ReadSerializedString())) then
                        failwith $"Duplicate plugin attribute '{attribute_name}'."
                | _ -> ()

    if Guid.Parse attributes["GuidAttribute"] <> Guid.Parse guid then
        failwith "Packaged plug-in GUID does not match AssemblyInfo.fs."

    if attributes["TargetFrameworkAttribute"] <> framework then
        failwith $"Packaged plug-in does not target '{framework}'."

    let rhino_references =
        [| for handle in reader.AssemblyReferences do
               let reference = reader.GetAssemblyReference handle

               if reader.GetString reference.Name = "RhinoCommon" then
                   yield reference.Version.ToString() |]

    if rhino_references <> [| rhino |] then
        failwith "Packaged plug-in uses an unexpected RhinoCommon version."

    if
        assembly.Version.ToString() <> version + ".0"
        || attributes["AssemblyFileVersionAttribute"] <> version + ".0"
        || attributes["AssemblyInformationalVersionAttribute"] <> version
    then
        failwith $"Packaged plugin version mismatch; expected {version} ({version}.0)."

if Path.GetFileName fsi.CommandLineArgs[0] = "check-plugin-metadata.fsx" then
    match fsi.CommandLineArgs |> Array.skip 1 with
    | [| path; name; version; framework; rhino; guid |] -> check_plugin path name version framework rhino guid
    | _ -> failwith "Expected plugin path, assembly name, version, framework, RhinoCommon version and plug-in GUID."
