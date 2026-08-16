module RhinosCanFly.Commands.RhinosCanFlyTempFlyHeld

open global.RhinosCanFly
open Rhino
open Rhino.Commands

let run (document: RhinoDoc) =
    FlightStart.run (FlightSessionMode.while_right_mouse_held FlightMode.Temporary) document
