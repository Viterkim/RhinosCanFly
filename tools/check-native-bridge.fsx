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
              "rcf_mac_capture_config_size", "RhinosCanFly.Platform.Mac.MacNative+CaptureConfig"
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
              "content", 92
              "source_time", 96
              "sequence", 104
              "session", 112
              "routing", 120
              "phase", 124
              "momentum", 128
              "reserved", 132
              "target_window", 136
              "navigation_modifiers", 144
              "press_id", 152 ]

        check_offsets
            (plugin.GetType("RhinosCanFly.Platform.Mac.MacNative+CaptureConfig", true))
            [ "session", 0
              "window", 8
              "exit_buttons", 12
              "terminal_count", 16
              "entry_buttons", 20
              "command_count", 24
              "configured", 28
              "command_keys", 161
              "quarantine", 294
              "appkit_owned", 427
              "terminal", 560
              "command", 2736
              "held_buttons", 4912
              "entry_code", 4916
              "entry_source_time", 4920
              "entry_modifiers", 4928
              "entry_mouse_button", 4936
              "entry_mouse_press_id", 4944
              "entry_mouse_source_time", 4952 ]

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
              "rcf_mac_raw_validate"
              "rcf_mac_raw_state"
              "rcf_mac_raw_drain"
              "rcf_mac_raw_pending"
              "rcf_mac_raw_boundary"
              "rcf_mac_raw_discard"
              "rcf_mac_raw_reconcile"
              "rcf_mac_raw_initial_key"
              "rcf_mac_raw_error"
              "rcf_mac_raw_started_at"
              "rcf_mac_raw_activate"
              "rcf_mac_raw_finish"
              "rcf_mac_raw_revoke"
              "rcf_mac_entry_context"
              "rcf_mac_raw_diagnostics"
              "rcf_mac_raw_guards_pending" ] do
            NativeLibrary.GetExport(library, symbol) |> ignore

        printfn "Mac bridge loads; managed/native layouts and exports match."
    finally
        NativeLibrary.Free library
| _ -> failwith "Expected host; or managed plugin path and native library path."
