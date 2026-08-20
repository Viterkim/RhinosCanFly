namespace RhinosCanFly

open System
open System.Runtime.InteropServices
open Rhino
open Rhino.Commands

[<AbstractClass>]
type PluginCommand(run: RhinoDoc -> Result) =
    inherit Command()

    override self.EnglishName =
        let className = self.GetType().Name
        let suffix = "Command"

        if not (className.EndsWith(suffix, StringComparison.Ordinal)) then
            invalidOp $"Rhino command class '{className}' must end with '{suffix}'."

        className.Substring(0, className.Length - suffix.Length)

    override _.CommandContextHelpUrl = "about:blank"
    override _.RunCommand(document: RhinoDoc, _mode: RunMode) = run document

[<Guid("D25AFA9B-C34C-49AC-8592-FB6A4B4061FE")>]
[<CommandStyle(Style.Transparent)>]
type RhinosCanFlyCommand() =
    inherit PluginCommand(Commands.RhinosCanFly.run)

[<Guid("38D5BD6A-334F-4038-A3C7-B374CBD760BB")>]
[<CommandStyle(Style.Transparent)>]
type RhinosCanFlyTempFlyCommand() =
    inherit PluginCommand(Commands.RhinosCanFlyTempFly.run)

[<Guid("D78B9DD9-30B0-45E5-9436-57C4253BA0C6")>]
[<CommandStyle(Style.Hidden ||| Style.Transparent ||| Style.DoNotRepeat)>]
type RhinosCanFlyHeldCommand() =
    inherit PluginCommand(Commands.RhinosCanFlyHeld.run)

[<Guid("D06ECC7F-7346-4112-9367-F1E9D7B228F7")>]
[<CommandStyle(Style.Hidden ||| Style.Transparent ||| Style.DoNotRepeat)>]
type RhinosCanFlyTempFlyHeldCommand() =
    inherit PluginCommand(Commands.RhinosCanFlyTempFlyHeld.run)

[<Guid("06912096-2514-4F29-9E35-A00D0D436334")>]
type RhinosCanFlyOptionsCommand() =
    inherit PluginCommand(Commands.RhinosCanFlyOptions.run)

[<Guid("EAB2EC5E-2183-4661-B523-30BB72EA12EE")>]
type RhinosCanFlySetSpeedCommand() =
    inherit PluginCommand(Commands.RhinosCanFlySetSpeed.run)

[<Guid("FBFEE882-412C-418A-8459-C5A088BF251B")>]
[<CommandStyle(Style.Transparent)>]
type RhinosCanFlyPivotCommand() =
    inherit PluginCommand(Commands.RhinosCanFlyPivot.run)

[<Guid("FA0A51EB-0D18-4835-8A18-94D574D92E4C")>]
[<CommandStyle(Style.Transparent)>]
type RhinosCanFlyPanCommand() =
    inherit PluginCommand(Commands.RhinosCanFlyPan.run)

[<Guid("E598A986-3A77-4C16-B35A-67FDDB9EF79C")>]
[<CommandStyle(Style.Hidden ||| Style.DoNotRepeat)>]
type RhinosCanFlyInputRecoverCommand() =
    inherit PluginCommand(Commands.RhinosCanFlyInputRecover.run)

[<Guid("73C463C6-A091-4BC8-B6C7-E5311817F0F8")>]
[<CommandStyle(Style.Transparent ||| Style.DoNotRepeat)>]
type RhinosCanFlyToggleEnableCommand() =
    inherit PluginCommand(Commands.RhinosCanFlyToggleEnable.run)
