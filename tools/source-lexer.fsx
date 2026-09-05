module SourceLexer

open System

type StringKind =
    | Quoted
    | Verbatim
    | Triple

type LexicalState =
    | Code
    | StringBody of StringKind * bool
    | HoleCode of StringKind * int
    | HoleFormat of StringKind
    | HoleString of StringKind * int
    | Character
    | LineComment
    | BlockComment of int

let code_only (source: string) =
    let result = Text.StringBuilder(source.Length)
    let mutable state = Code
    let mutable index = 0

    let starts_with (text: string) =
        source.AsSpan(index).StartsWith(text.AsSpan(), StringComparison.Ordinal)

    let blank_run (count: int) =
        for offset = 0 to count - 1 do
            let character = source[index + offset]
            result.Append(if character = '\n' then '\n' else ' ') |> ignore

        index <- index + count

    let keep_run (count: int) =
        result.Append(source, index, count) |> ignore
        index <- index + count

    let doubled_dollar () = index > 0 && source[index - 1] = '$'

    while index < source.Length do
        match state with
        | Code when starts_with "//" ->
            blank_run 2
            state <- LineComment
        | Code when starts_with "(*)" -> keep_run 3
        | Code when starts_with "(*" ->
            blank_run 2
            state <- BlockComment 1
        | Code when starts_with "$\"\"\"" && not (doubled_dollar ()) ->
            blank_run 4
            state <- StringBody(Triple, true)
        | Code when (starts_with "$@\"" || starts_with "@$\"") && not (doubled_dollar ()) ->
            blank_run 3
            state <- StringBody(Verbatim, true)
        | Code when starts_with "$\"" && not (doubled_dollar ()) ->
            blank_run 2
            state <- StringBody(Quoted, true)
        | Code when starts_with "\"\"\"" ->
            blank_run 3
            state <- StringBody(Triple, false)
        | Code when starts_with "@\"" ->
            blank_run 2
            state <- StringBody(Verbatim, false)
        | Code when source[index] = '"' ->
            blank_run 1
            state <- StringBody(Quoted, false)
        | Code when source[index] = '\'' ->
            let simple_character = index + 2 < source.Length && source[index + 2] = '\''

            let escaped_character =
                index + 3 < source.Length
                && source[index + 1] = '\\'
                && source[index + 3] = '\''

            if simple_character || escaped_character then
                blank_run 1
                state <- Character
            else
                keep_run 1
        | Code -> keep_run 1
        | LineComment when source[index] = '\n' ->
            blank_run 1
            state <- Code
        | LineComment -> blank_run 1
        | BlockComment depth when starts_with "(*" ->
            blank_run 2
            state <- BlockComment(depth + 1)
        | BlockComment depth when starts_with "*)" ->
            blank_run 2
            state <- if depth = 1 then Code else BlockComment(depth - 1)
        | BlockComment _ -> blank_run 1
        | StringBody(Triple, _) when starts_with "\"\"\"" ->
            blank_run 3
            state <- Code
        | StringBody(Verbatim, _) when starts_with "\"\"" -> blank_run 2
        | StringBody(Verbatim, _) when source[index] = '"' ->
            blank_run 1
            state <- Code
        | StringBody(Quoted, _) when source[index] = '\\' && index + 1 < source.Length -> blank_run 2
        | StringBody(Quoted, _) when source[index] = '"' ->
            blank_run 1
            state <- Code
        | StringBody(_, true) when starts_with "{{" || starts_with "}}" -> blank_run 2
        | StringBody(kind, true) when source[index] = '{' ->
            blank_run 1
            state <- HoleCode(kind, 0)
        | StringBody _ -> blank_run 1
        | HoleCode(kind, depth) when source[index] = '"' ->
            blank_run 1
            state <- HoleString(kind, depth)
        | HoleCode(kind, 0) when source[index] = '}' ->
            blank_run 1
            state <- StringBody(kind, true)
        | HoleCode(kind, 0) when source[index] = ':' || source[index] = ',' ->
            blank_run 1
            state <- HoleFormat kind
        | HoleCode(kind, depth) when source[index] = '(' || source[index] = '[' || source[index] = '{' ->
            keep_run 1
            state <- HoleCode(kind, depth + 1)
        | HoleCode(kind, depth) when source[index] = ')' || source[index] = ']' || source[index] = '}' ->
            keep_run 1
            state <- HoleCode(kind, depth - 1)
        | HoleCode _ -> keep_run 1
        | HoleFormat kind when source[index] = '}' ->
            blank_run 1
            state <- StringBody(kind, true)
        | HoleFormat _ -> blank_run 1
        | HoleString(_, _) when source[index] = '\\' && index + 1 < source.Length -> blank_run 2
        | HoleString(kind, depth) when source[index] = '"' ->
            blank_run 1
            state <- HoleCode(kind, depth)
        | HoleString _ -> blank_run 1
        | Character when source[index] = '\\' && index + 1 < source.Length -> blank_run 2
        | Character when source[index] = '\'' ->
            blank_run 1
            state <- Code
        | Character -> blank_run 1

    result.ToString()
