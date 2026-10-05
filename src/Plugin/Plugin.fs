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

            match PlatformLifecycle.prepare () with
            | Ok() -> ()
            | Error error -> report $"RhinosCanFly input backend unavailable: {error}"

            RuntimeSettings.initialize ()
        with error ->
            report $"RhinosCanFly initialization failed: {error.Message}"

    override _.LoadTime = PlugInLoadTime.AtStartup

    override _.OptionsDialogPages(pages: List<OptionsDialogPage>) =
        pages.Add(new RhinosCanFlyOptionsPage())

    override _.OnShutdown() =
        try
            FlightSession.shutdown ()
        with error ->
            report $"RhinosCanFly flight shutdown failed: {error.Message}"

        PlatformLifecycle.shutdown report

        try
            FlightSpeed.shutdown ()
        with error ->
            report $"RhinosCanFly speed lifecycle shutdown failed: {error.Message}"

        try
            RuntimeSettings.shutdown ()
        with error ->
            report $"RhinosCanFly settings lifecycle shutdown failed: {error.Message}"
