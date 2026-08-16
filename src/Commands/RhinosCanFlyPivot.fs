module RhinosCanFly.Commands.RhinosCanFlyPivot

open global.RhinosCanFly
open Rhino
open Rhino.Commands

let run (document: RhinoDoc) =
    StandaloneNavigation.run StandaloneNavigation.Mode.Pivot document
