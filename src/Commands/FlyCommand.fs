namespace RhinosCanFly

open System.Runtime.InteropServices
open System.Windows.Forms
open Rhino
open Rhino.Commands

[<Guid("D25AFA9B-C34C-49AC-8592-FB6A4B4061FE")>]
type RhinosCanFlyCommand() =
    inherit Command()
    override _.EnglishName = "RhinosCanFly"
    override _.RunCommand(document: RhinoDoc, _mode: RunMode) = Commands.run document

[<Guid("06912096-2514-4F29-9E35-A00D0D436334")>]
type RhinosCanFlyOptionsCommand() =
    inherit Command()
    override _.EnglishName = "RhinosCanFlyOptions"

    override _.RunCommand(_document: RhinoDoc, _mode: RunMode) =
        use dialog = new RhinosCanFlySettingsDialog()
        let owner = new NativeWindow()
        owner.AssignHandle(RhinoApp.MainWindowHandle())

        try
            dialog.ShowDialog owner |> ignore
        finally
            owner.ReleaseHandle()

        Result.Success

[<Guid("5E1A4D2C-7F60-4D59-9E18-864D20F137B8")>]
type RhinosCanFlyInitCommand() =
    inherit Command()
    override _.EnglishName = "RhinosCanFlyInit"

    override _.RunCommand(_document: RhinoDoc, _mode: RunMode) =
        match RightClickEntry.initialize_from_config () with
        | Ok() -> Result.Success
        | Error error ->
            RhinoApp.WriteLine $"RhinosCanFly initialization failed: {error}"
            Result.Failure
