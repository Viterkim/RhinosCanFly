namespace RhinosCanFly

open System
open System.IO
open System.Runtime.InteropServices
open Rhino
open Rhino.Commands
open RhinosCanFly.Platform.Mac

module MacInputDiagnostics =
    let run (document: RhinoDoc) =
        let directory = Path.GetDirectoryName typeof<MacNative.InputEvent>.Assembly.Location
        let library = Path.Combine(directory, "libRhinosCanFlyMac.dylib")
        RhinoApp.WriteLine $"Rhino: {RhinoApp.Version}"

        RhinoApp.WriteLine
            $"Runtime: {RuntimeInformation.FrameworkDescription}, {RuntimeInformation.ProcessArchitecture}"

        RhinoApp.WriteLine $"Bridge: {library}"

        try
            let native = MacNative.load ()
            RhinoApp.WriteLine $"Bridge ABI: {MacNative.BRIDGE_ABI} (loaded and verified)"

            let source =
                match native.raw_source.Invoke() with
                | 1u -> "GCMouse"
                | 2u -> "CoreGraphics (unaccelerated pointer)"
                | _ -> "none"

            RhinoApp.WriteLine $"Last motion source: {source}"

            RhinoApp.WriteLine
                $"Last mouse acquisition: {native.raw_discovered.Invoke()} discovered, {native.raw_rejected.Invoke()} occupied; motion available={native.raw_available.Invoke() > 0u}"

            RhinoApp.WriteLine $"Raw movement callbacks: {native.raw_motion_count.Invoke()}"
            let view = if isNull document then null else document.Views.ActiveView

            if isNull view then
                RhinoApp.WriteLine "No active viewport."
            else
                let sdk_window = native.view_window.Invoke view.RuntimeSerialNumber

                let handle_window =
                    if sdk_window <> 0n then
                        0n
                    else
                        native.window_from_handle.Invoke view.Handle

                let identity = PlatformInput.capture_viewport_host view

                let route =
                    if sdk_window <> 0n then "Rhino SDK"
                    elif handle_window <> 0n then "viewport native handle"
                    else "document Eto window"

                RhinoApp.WriteLine
                    $"Viewport: {view.RuntimeSerialNumber}, {view.ActiveViewportID}, floating={view.Floating}"

                RhinoApp.WriteLine $"Window route: {route}, window={identity.window}"
                let position = MacNative.cursor_position ()
                let viewport = view.ActiveViewport

                let client =
                    viewport.ScreenToClient(System.Drawing.Point(int position.x, int position.y))

                let origin = viewport.ClientToScreen(System.Drawing.Point(0, 0))

                RhinoApp.WriteLine
                    $"Native screen: ({position.x}, {position.y}); Rhino client: ({client.X}, {client.Y})"

                RhinoApp.WriteLine $"Viewport screen origin: ({origin.X}, {origin.Y}); bounds: {viewport.Bounds}"
        with error ->
            RhinoApp.WriteLine $"Mac input diagnostic: {error.Message}"

        Result.Success

[<Guid("2BC22F40-C88C-4E33-B877-C32252646378")>]
[<CommandStyle(Style.Transparent ||| Style.DoNotRepeat)>]
type RhinosCanFlyInputInfoCommand() =
    inherit Command()
    override _.EnglishName = "RhinosCanFlyInputInfo"
    override _.RunCommand(document: RhinoDoc, _mode: RunMode) = MacInputDiagnostics.run document
