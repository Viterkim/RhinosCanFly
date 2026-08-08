namespace RhinosCanFly

open System.Runtime.InteropServices
open Rhino
open Rhino.Commands

[<Guid("D25AFA9B-C34C-49AC-8592-FB6A4B4061FE")>]
[<CommandStyle(Style.Transparent)>]
type RhinosCanFlyCommand() =
    inherit Command()
    override _.EnglishName = "RhinosCanFly"
    override _.RunCommand(document: RhinoDoc, _mode: RunMode) = Commands.run document

[<Guid("06912096-2514-4F29-9E35-A00D0D436334")>]
type RhinosCanFlyOptionsCommand() =
    inherit Command()
    override _.EnglishName = "RhinosCanFlyOptions"

    override _.RunCommand(document: RhinoDoc, _mode: RunMode) =
        use dialog = new RhinosCanFlySettingsDialog()
        dialog.ShowForRhino document
        Result.Success

[<Guid("EAB2EC5E-2183-4661-B523-30BB72EA12EE")>]
type RhinosCanFlySetSpeedCommand() =
    inherit Command()
    override _.EnglishName = "RhinosCanFlySetSpeed"
    override _.RunCommand(document: RhinoDoc, _mode: RunMode) = Commands.set_speed document

[<Guid("0F6AC701-CC4B-48B1-AF33-2D739C0D0543")>]
type RhinosCanFlyRedrawModeCommand() =
    inherit Command()
    override _.EnglishName = "RhinosCanFlyRedrawMode"

    override _.RunCommand(_document: RhinoDoc, _mode: RunMode) =
        RhinoApp.WriteLine $"RhinosCanFly redraw mode: {FlightRedraw.toggle ()}."
        Result.Success
