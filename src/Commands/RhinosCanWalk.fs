module RhinosCanFly.Commands.RhinosCanWalk

open global.RhinosCanFly
open Rhino
open Rhino.Commands
open Rhino.Input
open Rhino.Input.Custom

let mutable lastEyeHeight = 1.75

let run (document: RhinoDoc) =
    let previousPrompt = RhinoApp.CommandPrompt
    use input = new GetNumber()
    input.SetCommandPrompt "Walking eye height"
    input.SetDefaultNumber lastEyeHeight
    input.SetLowerLimit(0., true)

    let getResult =
        try
            input.Get()
        finally
            RhinoApp.SetCommandPrompt previousPrompt

    match getResult with
    | GetResult.Number ->
        let eyeHeight = input.Number()
        lastEyeHeight <- eyeHeight
        FlightStart.run (FlightSessionMode.walk eyeHeight) document
    | GetResult.Cancel -> Result.Cancel
    | _ -> Result.Failure
