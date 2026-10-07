open System
open System.IO
open System.Reflection
open System.Runtime.InteropServices

[<UnmanagedFunctionPointer(CallingConvention.Cdecl)>]
type Size = delegate of unit -> uint32

let check_offsets (layout: Type) (fields: (string * int) list) =
    for name, expected in fields do
        if Marshal.OffsetOf(layout, name).ToInt64() <> int64 expected then
            failwith $"Managed field offset mismatch: {layout.FullName}.{name}."

match fsi.CommandLineArgs |> Array.skip 1 with
| [| "host" |] -> printfn "%O" RuntimeInformation.ProcessArchitecture
| [| plugin_path; library_path |] ->
    if not (OperatingSystem.IsMacOS()) then
        invalidOp "The Mac bridge load check requires macOS."

    let plugin = Assembly.LoadFrom(Path.GetFullPath plugin_path)
    let library = NativeLibrary.Load(Path.GetFullPath library_path)

    try
        let read_size (name: string) =
            Marshal.GetDelegateForFunctionPointer<Size>(NativeLibrary.GetExport(library, name)).Invoke()

        let expected_abi =
            plugin.GetType("RhinosCanFly.Platform.Mac.MacNative", true).GetField("BRIDGE_ABI").GetRawConstantValue()
            :?> uint32

        if read_size "rcf_mac_abi" <> expected_abi then
            failwith "Unexpected Mac bridge ABI."

        for symbol, type_name in
            [ "rcf_mac_event_size", "RhinosCanFly.Platform.Mac.MacNative+InputEvent"
              "rcf_mac_motion_size", "RhinosCanFly.RelativeMotion+Packet" ] do
            if read_size symbol <> uint32 (Marshal.SizeOf(plugin.GetType(type_name, true))) then
                failwith $"Native/managed layout mismatch: {type_name}."

        check_offsets
            (plugin.GetType("RhinosCanFly.Platform.Mac.MacNative+InputEvent", true))
            [ "kind", 0
              "code", 4
              "down", 8
              "repeated", 12
              "modifiers", 16
              "timestamp", 24
              "dx", 32
              "dy", 40
              "wheel", 48
              "precise", 56
              "inverted", 60
              "window", 64
              "screen_x", 72
              "screen_y", 80
              "buttons", 88
              "content", 92 ]

        check_offsets (plugin.GetType("RhinosCanFly.RelativeMotion+Packet", true)) [ "timestamp", 0; "dx", 8; "dy", 16 ]

        for symbol in
            [ "rcf_mac_uptime"
              "rcf_mac_keyboard_boundary"
              "rcf_mac_capture_key"
              "rcf_mac_capture_button"
              "rcf_mac_foreground_window"
              "rcf_mac_view_window"
              "rcf_mac_window_from_handle"
              "rcf_mac_monitor_begin"
              "rcf_mac_monitor_end"
              "rcf_mac_monitor_window"
              "rcf_mac_raw_begin"
              "rcf_mac_raw_end"
              "rcf_mac_raw_available"
              "rcf_mac_raw_source"
              "rcf_mac_raw_validate"
              "rcf_mac_raw_discovered"
              "rcf_mac_raw_rejected"
              "rcf_mac_raw_motion_count" ] do
            NativeLibrary.GetExport(library, symbol) |> ignore

        printfn "Mac bridge loads; managed/native layouts and exports match."
    finally
        NativeLibrary.Free library
| _ -> failwith "Expected host; or managed plugin path and native library path."
