module RhinosCanFly.Commands.RhinosCanFlyTempFlyRightClick

open global.RhinosCanFly
open Rhino
open Rhino.Commands

let run (document: RhinoDoc) =
    FlightStart.run (FlightSessionMode.until_exit_from_right_mouse FlightMode.Temporary) document
