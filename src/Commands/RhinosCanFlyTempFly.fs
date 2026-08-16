module RhinosCanFly.Commands.RhinosCanFlyTempFly

open global.RhinosCanFly
open Rhino
open Rhino.Commands

let run (document: RhinoDoc) =
    FlightStart.run (FlightSessionMode.until_exit FlightMode.Temporary) document
