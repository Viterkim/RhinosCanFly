namespace RhinosCanFly

open System.Collections.Generic
open Rhino.PlugIns
open Rhino.UI

type RhinosCanFlyPlugin() as self =
    inherit PlugIn()

    do
        Config.initialize self.SettingsDirectory
        RightClickEntry.initialize_from_config () |> ignore

    override _.LoadTime = PlugInLoadTime.AtStartup

    override _.OptionsDialogPages(pages: List<OptionsDialogPage>) =
        pages.Add(new RhinosCanFlyOptionsPage())
