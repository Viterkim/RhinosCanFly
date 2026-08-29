module RhinosCanFly.Commands.RhinosCanFlyUntiltView

open global.RhinosCanFly
open Rhino
open Rhino.Commands
open Rhino.Geometry

let run (document: RhinoDoc) =
    let view = document.Views.ActiveView

    if isNull view then
        RhinoApp.WriteLine "RhinosCanFlyUntiltView: no active view."
        Result.Failure
    else
        try
            let viewport = view.ActiveViewport
            let struct (_, up) = Movement.camera_basis viewport.CameraDirection Vector3d.ZAxis

            viewport.CameraUp <- up
            view.Redraw()
            Result.Success
        with error ->
            RhinoApp.WriteLine $"RhinosCanFlyUntiltView failed: {error.Message}"
            Result.Failure
