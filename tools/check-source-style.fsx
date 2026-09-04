open System
open System.IO
open System.Text.RegularExpressions

let source_root: string = fsi.CommandLineArgs |> Array.last

let source_files () =
    Directory.EnumerateFiles(source_root, "*", SearchOption.AllDirectories)
    |> Seq.filter (fun (path: string) ->
        let extension = Path.GetExtension path
        extension = ".fs" || extension = ".fsx")

let has_untyped_parameters (parameters: string) =
    let text = parameters.Trim()

    if String.IsNullOrWhiteSpace text then
        false
    else
        let groups = Regex.Matches(text, @"\((?<parameter>[^()]*)\)")
        let bare_parameters = Regex.Replace(text, @"\([^()]*\)", "").Trim()

        if groups.Count = 0 then
            true
        elif not (String.IsNullOrWhiteSpace bare_parameters) then
            true
        else
            groups
            |> Seq.cast<Match>
            |> Seq.exists (fun (group: Match) ->
                let parameter = group.Groups["parameter"].Value.Trim()
                not (String.IsNullOrWhiteSpace parameter) && not (parameter.Contains ':'))

let let_prefix =
    @"^\s*let\s+(?!mutable\b)(?>(?:(?:rec|inline|private|internal|public)\s+)*)(?!struct\b)"

let checks =
    [ "function",
      Regex(
          let_prefix
          + @"[A-Za-z_][\w']*(?:<[^>]+>)?\s+(?<parameters>(?:(?:\([^)]*\)|[A-Za-z_][\w']*)\s*)+)(?:\s*:\s*[^=]+)?\s*=",
          RegexOptions.Compiled
      )
      "member",
      Regex(
          @"^\s*(?:member|override)\s+[^.]+\.[A-Za-z_][\w']*\s+(?<parameters>(?:(?:\([^)]*\)|[A-Za-z_][\w']*)\s*)+)(?:\s*:\s*[^=]+)?\s*=",
          RegexOptions.Compiled
      )
      "constructor", Regex(@"^\s*type\s+[A-Za-z_][\w']*(?:<[^>]+>)?\s*(?<parameters>\([^)]*\))", RegexOptions.Compiled)
      "lambda", Regex(@"\bfun\s+(?<parameters>.*?)\s*->", RegexOptions.Compiled) ]

let violations_in_line (line: string) =
    checks
    |> Seq.choose (fun (kind: string, pattern: Regex) ->
        let matched = pattern.Match line

        if matched.Success && has_untyped_parameters matched.Groups["parameters"].Value then
            Some kind
        else
            None)
    |> Seq.toList

type FragmentEnd =
    | Equals
    | Arrow

type SourceFragment = { line_number: int; text: string }

let declaration_start =
    Regex(@"^\s*(?:let\b|member\b|override\b|type\s+[A-Za-z_][\w']*)", RegexOptions.Compiled)

let fragment_end (line: string) =
    if declaration_start.IsMatch line && not (line.Contains '=') then
        Some Equals
    elif Regex.IsMatch(line, @"\bfun\b") && not (line.Contains "->") then
        Some Arrow
    else
        None

let is_complete (ending: FragmentEnd) (text: string) =
    match ending with
    | Equals -> text.Contains '='
    | Arrow -> text.Contains "->"

let source_fragments (source: string) =
    let lines = source.Replace("\r\n", "\n").Split '\n'
    let fragments = ResizeArray<SourceFragment>()
    let mutable index = 0

    while index < lines.Length do
        let first_line_number = index + 1
        let first_line = lines[index]

        match fragment_end first_line with
        | None ->
            fragments.Add
                { line_number = first_line_number
                  text = first_line }

            index <- index + 1
        | Some ending ->
            let text = Text.StringBuilder(first_line.TrimEnd())
            index <- index + 1

            while index < lines.Length && not (is_complete ending (text.ToString())) do
                text.Append(' ').Append(lines[index].Trim()) |> ignore
                index <- index + 1

            fragments.Add
                { line_number = first_line_number
                  text = text.ToString() }

    fragments |> Seq.toList

let violations_in_source (source: string) =
    source_fragments source
    |> Seq.collect (fun (fragment: SourceFragment) ->
        violations_in_line fragment.text
        |> Seq.map (fun (kind: string) -> fragment.line_number, fragment.text, kind))
    |> Seq.toList

type LexicalState =
    | Code
    | String
    | VerbatimString
    | TripleString
    | Character
    | LineComment
    | BlockComment of int

let code_only (source: string) =
    let result = Text.StringBuilder(source.Length)
    let mutable state = Code
    let mutable index = 0

    let blank (character: char) =
        result.Append(if character = '\n' then '\n' else ' ') |> ignore

    let blank_pair () =
        blank source[index]
        blank source[index + 1]
        index <- index + 2

    let blank_triple () =
        blank source[index]
        blank source[index + 1]
        blank source[index + 2]
        index <- index + 3

    while index < source.Length do
        match state with
        | Code when source.AsSpan(index).StartsWith("//".AsSpan(), StringComparison.Ordinal) ->
            blank_pair ()
            state <- LineComment
        | Code when source.AsSpan(index).StartsWith("(*)".AsSpan(), StringComparison.Ordinal) ->
            result.Append("(*)") |> ignore
            index <- index + 3
        | Code when source.AsSpan(index).StartsWith("(*".AsSpan(), StringComparison.Ordinal) ->
            blank_pair ()
            state <- BlockComment 1
        | Code when source.AsSpan(index).StartsWith("\"\"\"".AsSpan(), StringComparison.Ordinal) ->
            blank_triple ()
            state <- TripleString
        | Code when source.AsSpan(index).StartsWith("@\"".AsSpan(), StringComparison.Ordinal) ->
            blank_pair ()
            state <- VerbatimString
        | Code when source[index] = '"' ->
            blank source[index]
            index <- index + 1
            state <- String
        | Code when source[index] = '\'' ->
            let simple_character = index + 2 < source.Length && source[index + 2] = '\''

            let escaped_character =
                index + 3 < source.Length
                && source[index + 1] = '\\'
                && source[index + 3] = '\''

            if simple_character || escaped_character then
                blank source[index]
                index <- index + 1
                state <- Character
            else
                result.Append(source[index]) |> ignore
                index <- index + 1
        | Code ->
            result.Append(source[index]) |> ignore
            index <- index + 1
        | LineComment when source[index] = '\n' ->
            blank source[index]
            index <- index + 1
            state <- Code
        | LineComment ->
            blank source[index]
            index <- index + 1
        | BlockComment depth when source.AsSpan(index).StartsWith("(*".AsSpan(), StringComparison.Ordinal) ->
            blank_pair ()
            state <- BlockComment(depth + 1)
        | BlockComment depth when source.AsSpan(index).StartsWith("*)".AsSpan(), StringComparison.Ordinal) ->
            blank_pair ()
            state <- if depth = 1 then Code else BlockComment(depth - 1)
        | BlockComment _ ->
            blank source[index]
            index <- index + 1
        | String when source[index] = '\\' && index + 1 < source.Length -> blank_pair ()
        | String when source[index] = '"' ->
            blank source[index]
            index <- index + 1
            state <- Code
        | String ->
            blank source[index]
            index <- index + 1
        | VerbatimString when source.AsSpan(index).StartsWith("\"\"".AsSpan(), StringComparison.Ordinal) ->
            blank_pair ()
        | VerbatimString when source[index] = '"' ->
            blank source[index]
            index <- index + 1
            state <- Code
        | VerbatimString ->
            blank source[index]
            index <- index + 1
        | TripleString when source.AsSpan(index).StartsWith("\"\"\"".AsSpan(), StringComparison.Ordinal) ->
            blank_triple ()
            state <- Code
        | TripleString ->
            blank source[index]
            index <- index + 1
        | Character when source[index] = '\\' && index + 1 < source.Length -> blank_pair ()
        | Character when source[index] = '\'' ->
            blank source[index]
            index <- index + 1
            state <- Code
        | Character ->
            blank source[index]
            index <- index + 1

    result.ToString()

let private_keyword_pattern = Regex(@"\bprivate\b", RegexOptions.Compiled)

let literal_declaration_pattern =
    Regex(@"\[<Literal>\]\s*let\s+(?:(?:private|internal|public)\s+)*(?<name>[A-Za-z_][\w']*)", RegexOptions.Compiled)

let yelling_snake_case_pattern = Regex(@"^[A-Z][A-Z0-9_]*$", RegexOptions.Compiled)

let lower_camel_identifier_pattern =
    Regex(@"(?<!\w)_*?(?<name>[a-z][A-Za-z0-9']*[A-Z][A-Za-z0-9']*)\b", RegexOptions.Compiled)

let allowed_lower_camel_identifiers =
    set [ "defaultArg"; "invalidArg"; "invalidOp"; "isNull"; "nullArg" ]

let allowed_lower_camel_qualifiers =
    set
        [ "Array"
          "Array2D"
          "Array3D"
          "Array4D"
          "Async"
          "LanguagePrimitives"
          "List"
          "Map"
          "NativePtr"
          "Operators"
          "Option"
          "Result"
          "Seq"
          "Set"
          "Unchecked"
          "ValueOption" ]

let qualifier_before (source: string) (identifier_index: int) =
    if identifier_index = 0 || source[identifier_index - 1] <> '.' then
        None
    else
        let mutable start = identifier_index - 2

        while start >= 0
              && (Char.IsLetterOrDigit source[start]
                  || source[start] = '_'
                  || source[start] = '\'') do
            start <- start - 1

        let qualifier = source.Substring(start + 1, identifier_index - start - 2)
        if qualifier = "" then None else Some qualifier

let lower_camel_identifier_is_allowed (source: string) (matched: Match) =
    let name = matched.Groups["name"].Value

    allowed_lower_camel_identifiers.Contains name
    || match qualifier_before source matched.Groups["name"].Index with
       | Some qualifier -> allowed_lower_camel_qualifiers.Contains qualifier
       | None -> false

let checker_self_tests =
    [ "let private sample_rate = 120L", false
      "let internal sample_value: int = 1", false
      "let rec private loop (value: int) = loop value", false
      "let inline private convert (value: int) = value", false
      "let mutable state = 0", false
      "let struct (left, right) = pair", false
      "let struct (left, right) =\n    pair", false
      "let private run () = ()", false
      "let private run value = value", true
      "let run left right = left + right", true
      "let run (left: int) right = left + right", true
      "let run (left: int) (right: int) = left + right", false
      "let value: int = 1", false
      "let rec private loop (value) = loop value", true
      "let inline public convert value = value", true
      "let run\n    (value: int)\n    = value", false
      "let run\n    value\n    = value", true
      "override _.Run\n    (value: int)\n    = value", false
      "override _.Run\n    (value)\n    = value", true
      "member _.Run value = value", true
      "member _.Run left right = left + right", true
      "member _.Run (left: int) (right: int) = left + right", false
      "let run =\n    fun\n        (value: int)\n        -> value", false
      "let run =\n    fun\n        value\n        -> value", true ]

for source, expects_violation in checker_self_tests do
    let has_violation = not (List.isEmpty (violations_in_source source))

    if has_violation <> expects_violation then
        failwith $"Explicit-input lint self-test failed for: {source}"

let private_keyword_self_tests =
    [ "let private value = 1", true
      "let mutable private value = 1", true
      "let rec private loop (value: int) = loop value", true
      "type private State = Ready", true
      "type State = private | Ready", true
      "let privateValue = 1", false
      "let value = 1", false
      "// let private value = 1", false
      "let text = \"private\"", false
      "(* private *) let value = 1", false ]

for source, expects_violation in private_keyword_self_tests do
    if private_keyword_pattern.IsMatch(code_only source) <> expects_violation then
        failwith $"No-private lint self-test failed for: {source}"

let literal_name_self_tests =
    [ "CURRENT_VERSION", true
      "WM_RBUTTONDOWN", true
      "BUTTON_4_UP", true
      "current_version", false
      "CurrentVersion", false
      "4_BUTTON", false ]

for name, expected in literal_name_self_tests do
    if yelling_snake_case_pattern.IsMatch(name) <> expected then
        failwith $"Literal-name lint self-test failed for: {name}"

let lower_camel_identifier_self_tests =
    [ "let cameraLocation = viewport.CameraLocation", [ "cameraLocation" ]
      "let camera_location = viewport.CameraLocation", []
      "let struct (currentX, currentY) = unpack pair", [ "currentX"; "currentY" ]
      "let run (minimumCapacity: uint32) = minimumCapacity", [ "minimumCapacity"; "minimumCapacity" ]
      "let _cameraLocation = viewport.CameraLocation", [ "cameraLocation" ]
      "let value = if isNull view then invalidOp text else value", []
      "let value = Option.defaultValue fallback value", []
      "let value = Project.headerSize", [ "headerSize" ]
      "let total = Array.map2 (*) left right\nlet cameraLocation = viewport.CameraLocation", [ "cameraLocation" ]
      "// let cameraLocation = value", []
      "let text = \"cameraLocation\"", [] ]

let lower_camel_identifiers (source: string) =
    let code = code_only source

    lower_camel_identifier_pattern.Matches code
    |> Seq.cast<Match>
    |> Seq.filter (lower_camel_identifier_is_allowed code >> not)
    |> Seq.map (fun (matched: Match) -> matched.Groups["name"].Value)
    |> Seq.toList

for source, expected in lower_camel_identifier_self_tests do
    let actual = lower_camel_identifiers source

    if actual <> expected then
        failwith $"Snake-case value lint self-test failed for: {source}; expected {expected}; got {actual}"

let violations =
    source_files ()
    |> Seq.collect (fun (path: string) ->
        File.ReadAllText path
        |> violations_in_source
        |> Seq.map (fun (line_number: int, line: string, kind: string) ->
            $"{path}({line_number}): {kind} input is missing an explicit type: {line.Trim()}"))
    |> Seq.toList

let private_keyword_violations =
    source_files ()
    |> Seq.collect (fun (path: string) ->
        let source = File.ReadAllText path
        let code = code_only source
        let source_lines = source.Replace("\r\n", "\n").Split '\n'

        private_keyword_pattern.Matches code
        |> Seq.cast<Match>
        |> Seq.map (fun (matched: Match) ->
            let line_number = code.AsSpan(0, matched.Index).Count '\n' + 1

            $"{path}({line_number}): the private keyword is not used in project source: {source_lines[line_number - 1].Trim()}"))
    |> Seq.toList

let literal_name_violations =
    source_files ()
    |> Seq.collect (fun (path: string) ->
        let source = File.ReadAllText path
        let code = code_only source

        literal_declaration_pattern.Matches code
        |> Seq.cast<Match>
        |> Seq.choose (fun (matched: Match) ->
            let name = matched.Groups["name"].Value

            if yelling_snake_case_pattern.IsMatch name then
                None
            else
                let line_number =
                    source.Substring(0, matched.Index)
                    |> Seq.filter (fun (character: char) -> character = '\n')
                    |> Seq.length
                    |> (+) 1

                Some $"{path}({line_number}): literal name must use YELLING_SNAKE_CASE: {name}"))
    |> Seq.toList

let lower_camel_identifier_violations =
    source_files ()
    |> Seq.collect (fun (path: string) ->
        let source = File.ReadAllText path
        let code = code_only source
        let source_lines = source.Replace("\r\n", "\n").Split '\n'

        lower_camel_identifier_pattern.Matches code
        |> Seq.cast<Match>
        |> Seq.filter (lower_camel_identifier_is_allowed code >> not)
        |> Seq.distinctBy (fun (matched: Match) -> matched.Groups["name"].Value)
        |> Seq.map (fun (matched: Match) ->
            let name = matched.Groups["name"].Value
            let line_number = code.AsSpan(0, matched.Index).Count '\n' + 1

            $"{path}({line_number}): value and parameter names must use snake_case: {name}: {source_lines[line_number - 1].Trim()}"))
    |> Seq.toList

for violation in violations do
    Console.Error.WriteLine violation

for violation in private_keyword_violations do
    Console.Error.WriteLine violation

for violation in literal_name_violations do
    Console.Error.WriteLine violation

for violation in lower_camel_identifier_violations do
    Console.Error.WriteLine violation

if
    not (List.isEmpty violations)
    || not (List.isEmpty private_keyword_violations)
    || not (List.isEmpty literal_name_violations)
    || not (List.isEmpty lower_camel_identifier_violations)
then
    Environment.Exit 1
