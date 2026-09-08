module RhinosCanFly.Commands.RhinosCanFlyHeld

open global.RhinosCanFly
open Rhino
open Rhino.Commands

let run (document: RhinoDoc) (mode: RunMode) =
    FlightStart.run_held FlightMode.Normal document mode
