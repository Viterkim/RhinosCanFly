namespace RhinosCanFly

open System.Runtime.InteropServices
open Rhino
open Rhino.Commands

module CommandHelp =
    [<Literal>]
    let blank_url = "about:blank"

[<Guid("D25AFA9B-C34C-49AC-8592-FB6A4B4061FE")>]
[<CommandStyle(Style.Transparent)>]
type RhinosCanFlyCommand() =
    inherit Command()
    override _.EnglishName = "RhinosCanFly"
    override _.CommandContextHelpUrl = CommandHelp.blank_url

    override _.RunCommand(document: RhinoDoc, _mode: RunMode) =
        Commands.run FlightSessionMode.Persistent document

[<Guid("D78B9DD9-30B0-45E5-9436-57C4253BA0C6")>]
[<CommandStyle(Style.Hidden ||| Style.Transparent ||| Style.DoNotRepeat)>]
type RhinosCanFlyHeldCommand() =
    inherit Command()
    override _.EnglishName = "RhinosCanFlyHeld"
    override _.CommandContextHelpUrl = CommandHelp.blank_url

    override _.RunCommand(document: RhinoDoc, _mode: RunMode) =
        Commands.run FlightSessionMode.WhileRightMouseHeld document

[<Guid("06912096-2514-4F29-9E35-A00D0D436334")>]
type RhinosCanFlyOptionsCommand() =
    inherit Command()
    override _.EnglishName = "RhinosCanFlyOptions"
    override _.CommandContextHelpUrl = CommandHelp.blank_url

    override _.RunCommand(document: RhinoDoc, _mode: RunMode) =
        use dialog = new RhinosCanFlySettingsDialog()
        dialog.ShowForRhino document
        Result.Success

[<Guid("EAB2EC5E-2183-4661-B523-30BB72EA12EE")>]
type RhinosCanFlySetSpeedCommand() =
    inherit Command()
    override _.EnglishName = "RhinosCanFlySetSpeed"
    override _.CommandContextHelpUrl = CommandHelp.blank_url
    override _.RunCommand(document: RhinoDoc, _mode: RunMode) = Commands.set_speed document

[<Guid("FBFEE882-412C-418A-8459-C5A088BF251B")>]
[<CommandStyle(Style.Transparent)>]
type RhinosCanFlyPivotCommand() =
    inherit Command()
    override _.EnglishName = "RhinosCanFlyPivot"
    override _.CommandContextHelpUrl = CommandHelp.blank_url
    override _.RunCommand(document: RhinoDoc, _mode: RunMode) = Commands.pivot document

[<Guid("FA0A51EB-0D18-4835-8A18-94D574D92E4C")>]
[<CommandStyle(Style.Transparent)>]
type RhinosCanFlyPanCommand() =
    inherit Command()
    override _.EnglishName = "RhinosCanFlyPan"
    override _.CommandContextHelpUrl = CommandHelp.blank_url
    override _.RunCommand(document: RhinoDoc, _mode: RunMode) = Commands.pan document
