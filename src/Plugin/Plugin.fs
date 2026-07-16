namespace RhinosCanFly

open System.Collections.Generic
open Rhino
open Rhino.PlugIns
open Rhino.UI

type RhinosCanFlyPlugin() as self =
    inherit PlugIn()

    do
        Config.initialize self.SettingsDirectory
        RightClickEntry.initialize_from_config () |> ignore

        match MouseButtonOverrides.initialize_from_config () with
        | Ok() -> ()
        | Error error -> RhinoApp.WriteLine $"RhinosCanFly mouse overrides disabled: {error}"

    override _.LoadTime = PlugInLoadTime.AtStartup

    override _.OptionsDialogPages(pages: List<OptionsDialogPage>) =
        pages.Add(new RhinosCanFlyOptionsPage())

    override _.OnShutdown() = MouseButtonOverrides.shutdown ()
