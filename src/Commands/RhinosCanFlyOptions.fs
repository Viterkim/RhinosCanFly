module RhinosCanFly.Commands.RhinosCanFlyOptions

open global.RhinosCanFly
open Rhino
open Rhino.Commands

let run (document: RhinoDoc) = Options.show document
