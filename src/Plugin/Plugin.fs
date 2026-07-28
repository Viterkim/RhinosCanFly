namespace RhinosCanFly

open System.Collections.Generic
open Rhino
open Rhino.PlugIns
open Rhino.UI

type RhinosCanFlyPlugin() as self =
    inherit PlugIn()

    do
        Config.initialize self.SettingsDirectory

        match LiveSettings.load_and_apply () with
        | Ok() -> ()
        | Error error -> RhinoApp.WriteLine $"RhinosCanFly settings unavailable: {error}"

    override _.LoadTime = PlugInLoadTime.AtStartup

    override _.OptionsDialogPages(pages: List<OptionsDialogPage>) =
        pages.Add(new RhinosCanFlyOptionsPage())

    override _.OnShutdown() =
        RightClickEntry.shutdown ()
        PlatformInput.shutdown_mouse_button_overrides ()
