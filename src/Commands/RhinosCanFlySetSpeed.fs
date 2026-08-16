module RhinosCanFly.Commands.RhinosCanFlySetSpeed

open global.RhinosCanFly
open Rhino
open Rhino.Commands

let run (document: RhinoDoc) = SpeedSelection.run document
