namespace RhinosCanFly

open System.Collections.Generic
open Rhino
open Rhino.PlugIns
open Rhino.UI

type RhinosCanFlyPlugin() as self =
    inherit PlugIn()

    do
        try
            ConfigStorage.initialize self.SettingsDirectory

            match RuntimeSettings.load_and_apply () with
            | Ok() -> ()
            | Error error -> RhinoApp.WriteLine $"RhinosCanFly settings unavailable: {error}"
        with error ->
            RhinoApp.WriteLine $"RhinosCanFly initialization failed: {error.Message}"

    override _.LoadTime = PlugInLoadTime.AtStartup

    override _.OptionsDialogPages(pages: List<OptionsDialogPage>) =
        pages.Add(new RhinosCanFlyOptionsPage())

    override _.OnShutdown() =
        try
            let struct (remaining, errors) = PlatformInput.retry_raw_input_cleanup ()

            for error in errors do
                RhinoApp.WriteLine $"RhinosCanFly raw-input recovery: {error}"

            if remaining > 0 then
                RhinoApp.WriteLine $"RhinosCanFly raw-input recovery still owns {remaining} cleanup item(s)."
        with error ->
            RhinoApp.WriteLine $"RhinosCanFly raw-input recovery failed: {error.Message}"

        try
            RuntimeSettings.shutdown ()
        with error ->
            RhinoApp.WriteLine $"RhinosCanFly settings lifecycle shutdown failed: {error.Message}"

        try
            FlightSpeed.shutdown ()
        with error ->
            RhinoApp.WriteLine $"RhinosCanFly speed lifecycle shutdown failed: {error.Message}"

        try
            RightClickEntry.shutdown ()
        with error ->
            RhinoApp.WriteLine $"RhinosCanFly right-click shutdown failed: {error.Message}"

        try
            PlatformInput.shutdown_mouse_button_overrides ()
        with error ->
            RhinoApp.WriteLine $"RhinosCanFly mouse override shutdown failed: {error.Message}"
