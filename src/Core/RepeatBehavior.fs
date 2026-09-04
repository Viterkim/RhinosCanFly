module RhinosCanFly.RepeatBehavior

open System
open Rhino.ApplicationSettings

let command_names =
    [| "RhinosCanFly"
       "RhinosCanFlyTempFly"
       "RhinosCanWalk"
       "RhinosCanFlyToggleEnable" |]

let contains_name (names: string array) (candidate: string) =
    names
    |> Array.exists (fun (name: string) -> String.Equals(name, candidate, StringComparison.OrdinalIgnoreCase))

let apply (do_not_repeat: bool) =
    let current =
        NeverRepeatList.CommandNames() |> Option.ofObj |> Option.defaultValue [||]

    if do_not_repeat then
        let missing =
            command_names
            |> Array.filter (fun (name: string) -> not (contains_name current name))

        let updated = Array.append current missing

        if updated <> current || not NeverRepeatList.UseNeverRepeatList then
            NeverRepeatList.SetList updated |> ignore
    else
        let updated =
            current
            |> Array.filter (fun (name: string) -> not (contains_name command_names name))

        if updated <> current then
            NeverRepeatList.SetList updated |> ignore
