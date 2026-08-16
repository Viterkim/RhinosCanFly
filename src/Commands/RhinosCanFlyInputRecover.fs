module RhinosCanFly.Commands.RhinosCanFlyInputRecover

open global.RhinosCanFly
open Rhino
open Rhino.Commands

let run (_document: RhinoDoc) = InputRecovery.run ()
