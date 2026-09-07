module RhinosCanFly.Commands.RhinosCanFlyPan

open global.RhinosCanFly
open Rhino
open Rhino.Commands

let run (document: RhinoDoc) =
    StandaloneNavigation.run ViewNavigationMode.Pan document
