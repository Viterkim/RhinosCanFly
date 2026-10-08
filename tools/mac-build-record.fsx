#load "mac-runtime-payload.fsx"

open ``Mac-runtime-payload``
open System
open System.IO
open System.Security.Cryptography
open System.Text
open System.Text.Json

type BuildRecord =
    { profile: string
      sdk: string
      inputs: string
      payload: PayloadFile array }

let input_hash (root: string) =
    let files =
        seq {
            for directory in [ "src"; "native/mac"; "scripts/mac"; "tools"; ".config" ] do
                yield! Directory.EnumerateFiles(Path.Combine(root, directory), "*", SearchOption.AllDirectories)

            yield! Directory.EnumerateFiles(root, "*.fsproj")
            yield! Directory.EnumerateFiles(root, "Directory.Build.*")

            for name in [ "global.json"; "NuGet.Config"; "native/relative-motion.h" ] do
                let path = Path.Combine(root, name)

                if File.Exists path then
                    yield path
        }
        |> Seq.filter (fun (path: string) ->
            not (
                Path
                    .GetRelativePath(root, path)
                    .Replace('\\', '/')
                    .StartsWith("src/Platform/Win/", StringComparison.Ordinal)
            ))
        |> Seq.map (fun (path: string) -> Path.GetRelativePath(root, path).Replace('\\', '/'), path)
        |> Seq.sortBy fst
        |> Seq.map (fun (name: string, path: string) -> $"{name}:{hash_file path}")
        |> String.concat "\n"

    Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes files))

match fsi.CommandLineArgs |> Array.skip 1 with
| [| "inputs"; root |] -> printfn "%s" (input_hash (Path.GetFullPath root))
| [| mode; root; output; sdk; _ |]
| [| mode; root; output; sdk |] when mode = "write" || mode = "check" ->
    let arguments = fsi.CommandLineArgs |> Array.skip 1

    if mode = "write" && arguments.Length <> 5 then
        failwith "Writing a Mac build record requires the input hash captured before compilation."

    let root = Path.GetFullPath root
    let output = Path.GetFullPath output

    let profile =
        Path.GetRelativePath(Path.Combine(root, "bin/mac"), output).Replace('\\', '/')

    if Path.IsPathRooted profile || profile.StartsWith("..", StringComparison.Ordinal) then
        failwith "The verified Mac build must be under bin/mac."

    let path = Path.Combine(output, ".verified-build.json")

    if mode = "check" && not (File.Exists path) then
        failwith "Unverified Mac build. Run scripts/mac/build.sh before installing."

    let expected =
        { profile = profile
          sdk = sdk
          inputs = input_hash root
          payload = inventory output }

    if mode = "write" then
        if expected.inputs <> arguments[4] then
            failwith "Mac build inputs changed during compilation. Run scripts/mac/build.sh again."

        let temporary = path + "." + Guid.NewGuid().ToString("N")

        try
            File.WriteAllText(temporary, JsonSerializer.Serialize expected)
            File.Move(temporary, path, true)
        finally
            if File.Exists temporary then
                File.Delete temporary

        printfn "Verified Mac build recorded: %s" profile
    else
        let recorded = JsonSerializer.Deserialize<BuildRecord>(File.ReadAllText path)

        if recorded <> expected then
            failwith "Mac build inputs or artifacts changed. Run scripts/mac/build.sh before installing."

        if arguments.Length = 5 then
            check_files arguments[4] recorded.payload

        printfn "Verified Mac build matches: %s" profile
| _ -> failwith "Expected inputs root; write root output SDK input-hash; or check root output SDK [stage]."
