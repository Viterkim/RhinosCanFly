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

let letPrefix =
    @"^\s*let\s+(?!mutable\b)(?>(?:(?:rec|inline|private|internal|public)\s+)*)"

let checks =
    [ "function",
      Regex(
          letPrefix
          + @"[A-Za-z_][\w']*(?:<[^>]+>)?\s*(?<parameters>(?:\([^)]*\)\s*)+)\s*=",
          RegexOptions.Compiled
      )
      "function",
      Regex(letPrefix + @"[A-Za-z_][\w']*(?:<[^>]+>)?\s+(?<parameters>[A-Za-z_][\w']*)\s*=", RegexOptions.Compiled)
      "member", Regex(@"^\s*(?:member|override)\s+[^.(]+\.[^(]+(?<parameters>\([^)]*\))", RegexOptions.Compiled)
      "constructor", Regex(@"^\s*type\s+[A-Za-z_][\w']*(?:<[^>]+>)?(?<parameters>\([^)]*\))", RegexOptions.Compiled)
      "lambda", Regex(@"\bfun\s+(?<parameters>.*?)\s*->", RegexOptions.Compiled) ]

let violationsInLine (line: string) =
    checks
    |> Seq.choose (fun (kind: string, pattern: Regex) ->
        let matched = pattern.Match line

        if matched.Success && hasUntypedParameters matched.Groups["parameters"].Value then
            Some kind
        else
            None)
    |> Seq.toList

// Tests
let checkerSelfTests =
    [ "let private sample_rate = 120L", false
      "let internal sample_value: int = 1", false
      "let rec private loop (value: int) = loop value", false
      "let inline private convert (value: int) = value", false
      "let mutable state = 0", false
      "let private run () = ()", false
      "let private run value = value", true
      "let rec private loop (value) = loop value", true
      "let inline public convert value = value", true ]

for line, expectsViolation in checkerSelfTests do
    let hasViolation = not (List.isEmpty (violationsInLine line))

    if hasViolation <> expectsViolation then
        failwith $"Explicit-input lint self-test failed for: {line}"

let violations =
    Directory.EnumerateFiles(sourceRoot, "*.fs", SearchOption.AllDirectories)
    |> Seq.collect (fun (path: string) ->
        File.ReadLines path
        |> Seq.mapi (fun (index: int) (line: string) -> index + 1, line)
        |> Seq.collect (fun (lineNumber: int, line: string) ->
            violationsInLine line
            |> Seq.map (fun (kind: string) ->
                $"{path}({lineNumber}): {kind} input is missing an explicit type: {line.Trim()}")))
    |> Seq.toList

for violation in violations do
    Console.Error.WriteLine violation

if not (List.isEmpty violations) then
    Environment.Exit 1
