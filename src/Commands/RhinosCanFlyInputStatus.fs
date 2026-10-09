module RhinosCanFly.Commands.RhinosCanFlyInputStatus

open global.RhinosCanFly
open Rhino
open Rhino.Commands

let run (_document: RhinoDoc) =
    let version = typeof<FlyConfig>.Assembly.GetName().Version
    let build = typeof<FlyConfig>.Module.ModuleVersionId
    RhinoApp.WriteLine $"RhinosCanFly {version} build={build}: {PlatformFlightKeyboard.input_status ()}"
    Result.Success
