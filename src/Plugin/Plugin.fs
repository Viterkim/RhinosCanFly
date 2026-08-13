namespace RhinosCanFly

open System.Diagnostics
open System.Collections.Generic
open Rhino
open Rhino.PlugIns
open Rhino.UI

type RhinosCanFlyPlugin() as self =
    inherit PlugIn()

    let report (message: string) =
        try
            RhinoApp.WriteLine message
        with error ->
            Debug.WriteLine $"{message}; output failed: {error.Message}"

    do
        try
            ConfigStorage.initialize self.SettingsDirectory

            match RuntimeSettings.load_and_apply () with
            | Ok() -> ()
            | Error error -> report $"RhinosCanFly settings unavailable: {error}"
        with error ->
            report $"RhinosCanFly initialization failed: {error.Message}"

    override _.LoadTime = PlugInLoadTime.AtStartup

    override _.OptionsDialogPages(pages: List<OptionsDialogPage>) =
        pages.Add(new RhinosCanFlyOptionsPage())

    override _.OnShutdown() =
        try
            let struct (remaining, errors) = PlatformInput.retry_raw_input_cleanup ()

            for error in errors do
                report $"RhinosCanFly raw-input recovery: {error}"

            if remaining > 0 then
                report $"RhinosCanFly raw-input recovery still owns {remaining} cleanup item(s)."
        with error ->
            report $"RhinosCanFly raw-input recovery failed: {error.Message}"

        try
            let struct (remaining, errors) = PlatformInput.retry_cursor_clip_cleanup ()

            for error in errors do
                report $"RhinosCanFly cursor-clip recovery: {error}"

            if remaining > 0 then
                report $"RhinosCanFly cursor-clip recovery still owns {remaining} cleanup item(s)."
        with error ->
            report $"RhinosCanFly cursor-clip recovery failed: {error.Message}"

        try
            FlightSpeed.shutdown ()
        with error ->
            report $"RhinosCanFly speed lifecycle shutdown failed: {error.Message}"

        try
            RuntimeSettings.shutdown ()
        with error ->
            report $"RhinosCanFly settings lifecycle shutdown failed: {error.Message}"

        try
            RightClickEntry.shutdown ()
        with error ->
            report $"RhinosCanFly right-click shutdown failed: {error.Message}"

        try
            PlatformInput.shutdown_mouse_button_overrides ()
        with error ->
            report $"RhinosCanFly mouse override shutdown failed: {error.Message}"
