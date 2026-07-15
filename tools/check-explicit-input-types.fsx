open System
open System.IO
open System.Text.RegularExpressions

let sourceRoot: string = fsi.CommandLineArgs |> Array.last

let hasUntypedParameters (parameters: string) =
    let text = parameters.Trim()

    if String.IsNullOrWhiteSpace text then
        false
    else
        let groups = Regex.Matches(text, @"\((?<parameter>[^()]*)\)")

        if groups.Count = 0 then
            true
        else
            groups
            |> Seq.cast<Match>
            |> Seq.exists (fun (group: Match) ->
                let parameter = group.Groups["parameter"].Value.Trim()
                not (String.IsNullOrWhiteSpace parameter) && not (parameter.Contains ':'))

let checks =
    [ "function",
      Regex(
          @"^\s*let\s+(?!mutable\b)(?:rec\s+)?[A-Za-z_][\w']*(?:<[^>]+>)?\s*(?<parameters>(?:\([^)]*\)\s*)+)\s*=",
          RegexOptions.Compiled
      )
      "function",
      Regex(
          @"^\s*let\s+(?!mutable\b)(?:rec\s+)?[A-Za-z_][\w']*(?:<[^>]+>)?\s+(?<parameters>[A-Za-z_][\w']*)\s*=",
          RegexOptions.Compiled
      )
      "member", Regex(@"^\s*(?:member|override)\s+[^.(]+\.[^(]+(?<parameters>\([^)]*\))", RegexOptions.Compiled)
      "constructor", Regex(@"^\s*type\s+[A-Za-z_][\w']*(?:<[^>]+>)?(?<parameters>\([^)]*\))", RegexOptions.Compiled)
      "lambda", Regex(@"\bfun\s+(?<parameters>.*?)\s*->", RegexOptions.Compiled) ]

let violations =
    Directory.EnumerateFiles(sourceRoot, "*.fs", SearchOption.AllDirectories)
    |> Seq.collect (fun (path: string) ->
        File.ReadLines path
        |> Seq.mapi (fun (index: int) (line: string) -> index + 1, line)
        |> Seq.collect (fun (lineNumber: int, line: string) ->
            checks
            |> Seq.choose (fun (kind: string, pattern: Regex) ->
                let matched = pattern.Match line

                if matched.Success && hasUntypedParameters matched.Groups["parameters"].Value then
                    Some $"{path}({lineNumber}): {kind} input is missing an explicit type: {line.Trim()}"
                else
                    None)))
    |> Seq.toList

for violation in violations do
    Console.Error.WriteLine violation

if not (List.isEmpty violations) then
    Environment.Exit 1
