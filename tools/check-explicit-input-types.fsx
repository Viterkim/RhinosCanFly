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
          + @"[A-Za-z_][\w']*(?:<[^>]+>)?\s*(?<parameters>(?:\([^)]*\)\s*)+)(?:\s*:\s*[^=]+)?\s*=",
          RegexOptions.Compiled
      )
      "function",
      Regex(letPrefix + @"[A-Za-z_][\w']*(?:<[^>]+>)?\s+(?<parameters>[A-Za-z_][\w']*)\s*=", RegexOptions.Compiled)
      "member",
      Regex(
          @"^\s*(?:member|override)\s+[^.(]+\.[^(]+(?<parameters>(?:\([^)]*\)\s*)+)(?:\s*:\s*[^=]+)?\s*=",
          RegexOptions.Compiled
      )
      "constructor", Regex(@"^\s*type\s+[A-Za-z_][\w']*(?:<[^>]+>)?\s*(?<parameters>\([^)]*\))", RegexOptions.Compiled)
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

type FragmentEnd =
    | Equals
    | Arrow

type SourceFragment = { line_number: int; text: string }

let declarationStart =
    Regex(@"^\s*(?:let\b|member\b|override\b|type\s+[A-Za-z_][\w']*)", RegexOptions.Compiled)

let fragmentEnd (line: string) =
    if declarationStart.IsMatch line && not (line.Contains '=') then
        Some Equals
    elif Regex.IsMatch(line, @"\bfun\b") && not (line.Contains "->") then
        Some Arrow
    else
        None

let isComplete (ending: FragmentEnd) (text: string) =
    match ending with
    | Equals -> text.Contains '='
    | Arrow -> text.Contains "->"

let sourceFragments (source: string) =
    let lines = source.Replace("\r\n", "\n").Split '\n'
    let fragments = ResizeArray<SourceFragment>()
    let mutable index = 0

    while index < lines.Length do
        let firstLineNumber = index + 1
        let firstLine = lines[index]

        match fragmentEnd firstLine with
        | None ->
            fragments.Add
                { line_number = firstLineNumber
                  text = firstLine }

            index <- index + 1
        | Some ending ->
            let text = Text.StringBuilder(firstLine.TrimEnd())
            index <- index + 1

            while index < lines.Length && not (isComplete ending (text.ToString())) do
                text.Append(' ').Append(lines[index].Trim()) |> ignore
                index <- index + 1

            fragments.Add
                { line_number = firstLineNumber
                  text = text.ToString() }

    fragments |> Seq.toList

let violationsInSource (source: string) =
    sourceFragments source
    |> Seq.collect (fun (fragment: SourceFragment) ->
        violationsInLine fragment.text
        |> Seq.map (fun (kind: string) -> fragment.line_number, fragment.text, kind))
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
      "let inline public convert value = value", true
      "let run\n    (value: int)\n    = value", false
      "let run\n    value\n    = value", true
      "override _.Run\n    (value: int)\n    = value", false
      "override _.Run\n    (value)\n    = value", true
      "let run =\n    fun\n        (value: int)\n        -> value", false
      "let run =\n    fun\n        value\n        -> value", true ]

for source, expectsViolation in checkerSelfTests do
    let hasViolation = not (List.isEmpty (violationsInSource source))

    if hasViolation <> expectsViolation then
        failwith $"Explicit-input lint self-test failed for: {source}"

let violations =
    Directory.EnumerateFiles(sourceRoot, "*.fs", SearchOption.AllDirectories)
    |> Seq.collect (fun (path: string) ->
        File.ReadAllText path
        |> violationsInSource
        |> Seq.map (fun (lineNumber: int, line: string, kind: string) ->
            $"{path}({lineNumber}): {kind} input is missing an explicit type: {line.Trim()}"))
    |> Seq.toList

for violation in violations do
    Console.Error.WriteLine violation

if not (List.isEmpty violations) then
    Environment.Exit 1
