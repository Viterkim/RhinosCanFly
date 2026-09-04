module RhinosCanFly.Commands.RhinosCanWalk

open global.RhinosCanFly
open Rhino
open Rhino.Commands
open Rhino.Input
open Rhino.Input.Custom

let mutable last_eye_height = 1.75

let run (document: RhinoDoc) =
    let previous_prompt = RhinoApp.CommandPrompt
    use input = new GetNumber()
    input.SetCommandPrompt "Walking eye height"
    input.SetDefaultNumber last_eye_height
    input.SetLowerLimit(0., true)

    let get_result =
        try
            input.Get()
        finally
            RhinoApp.SetCommandPrompt previous_prompt

    match get_result with
    | GetResult.Number ->
        let eye_height = input.Number()
        last_eye_height <- eye_height
        FlightStart.run (FlightSessionMode.walk eye_height) document
    | GetResult.Cancel -> Result.Cancel
    | _ -> Result.Failure
