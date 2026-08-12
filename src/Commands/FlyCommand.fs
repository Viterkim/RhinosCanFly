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
        Commands.run (FlightSessionMode.until_exit FlightMode.Normal) document

[<Guid("38D5BD6A-334F-4038-A3C7-B374CBD760BB")>]
[<CommandStyle(Style.Transparent)>]
type RhinosCanFlyTempFlyCommand() =
    inherit Command()
    override _.EnglishName = "RhinosCanFlyTempFly"
    override _.CommandContextHelpUrl = CommandHelp.blank_url

    override _.RunCommand(document: RhinoDoc, _mode: RunMode) =
        Commands.run (FlightSessionMode.until_exit FlightMode.Temporary) document

[<Guid("D78B9DD9-30B0-45E5-9436-57C4253BA0C6")>]
[<CommandStyle(Style.Hidden ||| Style.Transparent ||| Style.DoNotRepeat)>]
type RhinosCanFlyHeldCommand() =
    inherit Command()
    override _.EnglishName = "RhinosCanFlyHeld"
    override _.CommandContextHelpUrl = CommandHelp.blank_url

    override _.RunCommand(document: RhinoDoc, _mode: RunMode) =
        Commands.run (FlightSessionMode.while_right_mouse_held FlightMode.Normal) document

[<Guid("D06ECC7F-7346-4112-9367-F1E9D7B228F7")>]
[<CommandStyle(Style.Hidden ||| Style.Transparent ||| Style.DoNotRepeat)>]
type RhinosCanFlyTempFlyHeldCommand() =
    inherit Command()
    override _.EnglishName = "RhinosCanFlyTempFlyHeld"
    override _.CommandContextHelpUrl = CommandHelp.blank_url

    override _.RunCommand(document: RhinoDoc, _mode: RunMode) =
        Commands.run (FlightSessionMode.while_right_mouse_held FlightMode.Temporary) document

[<Guid("06912096-2514-4F29-9E35-A00D0D436334")>]
type RhinosCanFlyOptionsCommand() =
    inherit Command()
    override _.EnglishName = "RhinosCanFlyOptions"
    override _.CommandContextHelpUrl = CommandHelp.blank_url

    override _.RunCommand(document: RhinoDoc, _mode: RunMode) = Commands.show_options document

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

[<Guid("6C944723-2787-41AD-9332-B68EBF068B26")>]
[<CommandStyle(Style.DoNotRepeat)>]
type RhinosCanFlyInputDiagnosticsCommand() =
    inherit Command()
    override _.EnglishName = "RhinosCanFlyInputDiagnostics"
    override _.CommandContextHelpUrl = CommandHelp.blank_url
    override _.RunCommand(_document: RhinoDoc, _mode: RunMode) = Commands.show_input_diagnostics ()

[<Guid("E598A986-3A77-4C16-B35A-67FDDB9EF79C")>]
[<CommandStyle(Style.DoNotRepeat)>]
type RhinosCanFlyInputRecoverCommand() =
    inherit Command()
    override _.EnglishName = "RhinosCanFlyInputRecover"
    override _.CommandContextHelpUrl = CommandHelp.blank_url
    override _.RunCommand(_document: RhinoDoc, _mode: RunMode) = Commands.recover_input ()
