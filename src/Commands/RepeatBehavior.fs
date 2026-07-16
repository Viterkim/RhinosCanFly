module RhinosCanFly.RepeatBehavior

open System
open Rhino.ApplicationSettings

let commandNames = [| "RhinosCanFly"; "RhinosCanFlyInit" |]

let contains_name (names: string array) (candidate: string) =
    names
    |> Array.exists (fun (name: string) -> String.Equals(name, candidate, StringComparison.OrdinalIgnoreCase))

let is_plugin_command (name: string) = contains_name commandNames name

let apply (doNotRepeat: bool) =
    let current =
        NeverRepeatList.CommandNames() |> Option.ofObj |> Option.defaultValue [||]

    if doNotRepeat then
        let missing =
            commandNames
            |> Array.filter (fun (name: string) -> not (contains_name current name))

        if missing.Length > 0 || not NeverRepeatList.UseNeverRepeatList then
            NeverRepeatList.SetList(Array.append current missing) |> ignore
    else
        let updated =
            current |> Array.filter (fun (name: string) -> not (is_plugin_command name))

        if updated.Length <> current.Length then
            NeverRepeatList.SetList updated |> ignore
