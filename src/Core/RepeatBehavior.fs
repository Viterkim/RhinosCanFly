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

let mutable owned_names: string array = [||]

let update_list (required: string array) (current: string array) (owned: string array) =
    let retained =
        current
        |> Array.filter (fun (name: string) -> not (contains_name owned name) || contains_name required name)

    let missing =
        required
        |> Array.filter (fun (name: string) -> not (contains_name retained name))

    let still_owned = owned |> Array.filter (contains_name retained)
    struct (Array.append retained missing, Array.append still_owned missing)

let apply_required (required: string array) =
    if NeverRepeatList.UseNeverRepeatList then
        let current =
            NeverRepeatList.CommandNames() |> Option.ofObj |> Option.defaultValue [||]

        let struct (updated, owned) = update_list required current owned_names

        if updated <> current then
            NeverRepeatList.SetList updated |> ignore

        owned_names <- owned
    else
        owned_names <- [||]

let apply (do_not_repeat: bool) =
    let internal_commands =
        [| "RhinosCanFlyHeld"; "RhinosCanFlyTempFlyHeld"; "RhinosCanFlyInputRecover" |]

    apply_required (
        if do_not_repeat then
            Array.append internal_commands command_names
        else
            internal_commands
    )

let shutdown () = apply_required [||]
