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

            match PlatformRawInput.prepare () with
            | Ok() -> ()
            | Error error -> report $"RhinosCanFly raw-input worker unavailable: {error}"

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
            FlightSession.shutdown ()
        with error ->
            report $"RhinosCanFly flight shutdown failed: {error.Message}"

        try
            PlatformMouseActions.shutdown ()
        with error ->
            report $"RhinosCanFly mouse override shutdown failed: {error.Message}"

        try
            match PlatformFlightKeyboard.shutdown () with
            | Ok() -> ()
            | Error error -> report $"RhinosCanFly keyboard hook shutdown failed: {error}"
        with error ->
            report $"RhinosCanFly keyboard hook shutdown failed: {error.Message}"

        try
            let struct (remaining, errors) = PlatformRawInput.retry_recovery ()

            for error in errors do
                report $"RhinosCanFly raw-input recovery: {error}"

            if remaining > 0 then
                report $"RhinosCanFly raw-input recovery still owns {remaining} cleanup item(s)."
        with error ->
            report $"RhinosCanFly raw-input recovery failed: {error.Message}"

        try
            for error in PlatformRawInput.shutdown () do
                report $"RhinosCanFly raw-input worker shutdown: {error}"
        with error ->
            report $"RhinosCanFly raw-input worker shutdown failed: {error.Message}"

        try
            let struct (remaining, errors) = PlatformCursorClip.retry_cleanup ()

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
