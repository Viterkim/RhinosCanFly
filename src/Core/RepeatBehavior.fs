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

    let internal_commands =
        [| "RhinosCanFlyHeld"; "RhinosCanFlyTempFlyHeld"; "RhinosCanFlyInputRecover" |]

    if do_not_repeat || NeverRepeatList.UseNeverRepeatList then
        let retained =
            if do_not_repeat then
                current
            else
                current
                |> Array.filter (fun (name: string) -> not (contains_name command_names name))

        let required =
            if do_not_repeat then
                Array.append internal_commands command_names
            else
                internal_commands

        let missing =
            required
            |> Array.filter (fun (name: string) -> not (contains_name retained name))

        let updated = Array.append retained missing

        if updated <> current || not NeverRepeatList.UseNeverRepeatList then
            NeverRepeatList.SetList updated |> ignore
