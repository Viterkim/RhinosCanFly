module RhinosCanFly.Commands.RhinosCanFlyTempFlyHeld

open global.RhinosCanFly
open Rhino
open Rhino.Commands

let run (document: RhinoDoc) (mode: RunMode) =
    FlightStart.run_held FlightMode.Temporary document mode
