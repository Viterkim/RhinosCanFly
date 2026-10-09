module RhinosCanFly.Platform.Mac.MacNative

open System
open System.IO
open System.Runtime.InteropServices
open RhinosCanFly

[<Literal>]
let BRIDGE_ABI = 5u

[<Literal>]
let CORE_GRAPHICS = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics"

[<Literal>]
let CORE_FOUNDATION =
    "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation"

[<Struct; StructLayout(LayoutKind.Sequential)>]
type Point =
    val mutable x: float
    val mutable y: float
    new(x: float, y: float) = { x = x; y = y }

[<Struct; StructLayout(LayoutKind.Sequential)>]
type InputEvent =
    val mutable kind: uint32
    val mutable code: uint32
    val mutable down: uint32
    val mutable repeated: uint32
    val mutable modifiers: uint64
    val mutable timestamp: float
    val mutable dx: float
    val mutable dy: float
    val mutable wheel: float
    val mutable precise: uint32
    val mutable inverted: uint32
    val mutable window: nativeint
    val mutable screen_x: float
    val mutable screen_y: float
    val mutable buttons: uint32
    val mutable content: uint32
    val mutable source_time: uint64
    val mutable sequence: uint64
    val mutable session: uint64
    val mutable routing: uint32
    val mutable phase: uint32
    val mutable momentum: uint32
    val mutable reserved: uint32
    val mutable target_window: uint32
    val mutable navigation_modifiers: uint64
    val mutable press_id: uint64

[<Struct; StructLayout(LayoutKind.Sequential)>]
type CaptureBinding =
    val mutable count: uint32

    [<MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)>]
    val mutable keys: uint32 array

[<Struct; StructLayout(LayoutKind.Sequential)>]
type CaptureConfig =
    val mutable session: uint64
    val mutable window: uint32
    val mutable exit_buttons: uint32
    val mutable terminal_count: uint32
    val mutable entry_buttons: uint32
    val mutable command_count: uint32

    [<MarshalAs(UnmanagedType.ByValArray, SizeConst = 133)>]
    val mutable configured: byte array

    [<MarshalAs(UnmanagedType.ByValArray, SizeConst = 133)>]
    val mutable command_keys: byte array

    [<MarshalAs(UnmanagedType.ByValArray, SizeConst = 133)>]
    val mutable quarantine: byte array

    [<MarshalAs(UnmanagedType.ByValArray, SizeConst = 133)>]
    val mutable appkit_owned: byte array

    [<MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)>]
    val mutable terminal: CaptureBinding array

    [<MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)>]
    val mutable command: CaptureBinding array

    val mutable held_buttons: uint32
    val mutable entry_code: uint32
    val mutable entry_source_time: uint64
    val mutable entry_modifiers: uint64
    val mutable entry_mouse_button: uint32
    val mutable entry_mouse_press_id: uint64
    val mutable entry_mouse_source_time: uint64

[<DllImport(CORE_GRAPHICS)>]
extern int CGAssociateMouseAndMouseCursorPosition(uint32 connected)

[<DllImport(CORE_GRAPHICS)>]
extern int CGDisplayHideCursor(uint32 display)

[<DllImport(CORE_GRAPHICS)>]
extern int CGDisplayShowCursor(uint32 display)

[<DllImport(CORE_GRAPHICS)>]
extern int CGWarpMouseCursorPosition(Point point)

[<DllImport(CORE_GRAPHICS)>]
extern nativeint CGEventCreate(nativeint source)

[<DllImport(CORE_GRAPHICS)>]
extern nativeint CGEventSourceCreate(int source)

[<DllImport(CORE_GRAPHICS)>]
extern float CGEventSourceGetPixelsPerLine(nativeint source)

[<DllImport(CORE_GRAPHICS)>]
extern Point CGEventGetLocation(nativeint event)

[<DllImport(CORE_GRAPHICS)>]
[<return: MarshalAs(UnmanagedType.I1)>]
extern bool CGEventSourceKeyState(int source, uint16 key)

[<DllImport(CORE_GRAPHICS)>]
[<return: MarshalAs(UnmanagedType.I1)>]
extern bool CGEventSourceButtonState(int source, uint32 button)

[<DllImport(CORE_FOUNDATION)>]
extern void CFRelease(nativeint value)

[<UnmanagedFunctionPointer(CallingConvention.Cdecl)>]
type Handler = delegate of event: byref<InputEvent> -> uint32

[<UnmanagedFunctionPointer(CallingConvention.Cdecl)>]
type Window = delegate of unit -> nativeint

[<UnmanagedFunctionPointer(CallingConvention.Cdecl)>]
type ViewWindow = delegate of uint32 -> nativeint

[<UnmanagedFunctionPointer(CallingConvention.Cdecl)>]
type NativeWindow = delegate of nativeint -> nativeint

[<UnmanagedFunctionPointer(CallingConvention.Cdecl)>]
type SetWindow = delegate of nativeint -> int32

[<UnmanagedFunctionPointer(CallingConvention.Cdecl)>]
type Notify = delegate of unit -> unit

[<UnmanagedFunctionPointer(CallingConvention.Cdecl)>]
type RawBegin = delegate of RelativeMotion.Handler * Notify * uint32 -> int32

[<UnmanagedFunctionPointer(CallingConvention.Cdecl)>]
type RawEnd = delegate of unit -> int32

[<UnmanagedFunctionPointer(CallingConvention.Cdecl)>]
type Begin = delegate of Handler * nativeint -> int32

[<UnmanagedFunctionPointer(CallingConvention.Cdecl)>]
type End = delegate of unit -> int32

[<UnmanagedFunctionPointer(CallingConvention.Cdecl)>]
type Size = delegate of unit -> uint32

[<UnmanagedFunctionPointer(CallingConvention.Cdecl)>]
type Time = delegate of unit -> float

[<UnmanagedFunctionPointer(CallingConvention.Cdecl)>]
type Discard = delegate of unit -> unit

[<UnmanagedFunctionPointer(CallingConvention.Cdecl)>]
type InitialKey = delegate of uint32 -> uint32

[<UnmanagedFunctionPointer(CallingConvention.Cdecl)>]
type Reconcile = delegate of uint32 -> unit

[<UnmanagedFunctionPointer(CallingConvention.Cdecl)>]
type Activate = delegate of byref<CaptureConfig> * nativeint -> int32

[<UnmanagedFunctionPointer(CallingConvention.Cdecl)>]
type Finish = delegate of uint32 -> int32

[<UnmanagedFunctionPointer(CallingConvention.Cdecl)>]
type EntryContext = delegate of byref<InputEvent> -> uint32

[<UnmanagedFunctionPointer(CallingConvention.Cdecl)>]
type Diagnostics = delegate of [<Out>] destination: byte array * capacity: uint32 -> uint32

type Api =
    { foreground_window: Window
      view_window: ViewWindow
      window_from_handle: NativeWindow
      begin_monitor: Begin
      end_monitor: End
      monitor_window: SetWindow
      raw_begin: RawBegin
      raw_end: RawEnd
      raw_available: Size
      raw_validate: Size
      raw_state: Size
      raw_drain: Size
      raw_pending: Size
      raw_boundary: Time
      raw_discard: Discard
      raw_reconcile: Reconcile
      raw_initial_key: InitialKey
      raw_error: Size
      raw_started_at: Time
      raw_activate: Activate
      raw_finish: Finish
      raw_revoke: Discard
      entry_context: EntryContext
      raw_diagnostics: Diagnostics
      raw_guards_pending: Size
      uptime: Time
      keyboard_boundary: Time
      capture_key: Size
      capture_button: Size }

let mutable api: Api option = None

let export<'t when 't :> Delegate> (library: nativeint) (name: string) =
    Marshal.GetDelegateForFunctionPointer<'t>(NativeLibrary.GetExport(library, name))

let validate_bridge (expected: uint32) (abi: uint32) (event_size: uint32) (motion_size: uint32) (config_size: uint32) =
    if
        abi <> expected
        || event_size <> uint32 (Marshal.SizeOf<InputEvent>())
        || motion_size <> uint32 (Marshal.SizeOf<RelativeMotion.Packet>())
        || config_size <> uint32 (Marshal.SizeOf<CaptureConfig>())
    then
        invalidOp "The Mac input bridge does not match this build."

let load () =
    match api with
    | Some loaded -> loaded
    | None ->
        if not (OperatingSystem.IsMacOS()) then
            invalidOp "The Mac input backend requires macOS."

        if not (OperatingSystem.IsMacOSVersionAtLeast 14) then
            invalidOp "Mac raw input requires macOS 14 or later."

        let directory = Path.GetDirectoryName typeof<InputEvent>.Assembly.Location

        let library =
            NativeLibrary.Load(Path.Combine(directory, "libRhinosCanFlyMac.dylib"))

        try
            validate_bridge
                BRIDGE_ABI
                ((export<Size> library "rcf_mac_abi").Invoke())
                ((export<Size> library "rcf_mac_event_size").Invoke())
                ((export<Size> library "rcf_mac_motion_size").Invoke())
                ((export<Size> library "rcf_mac_capture_config_size").Invoke())

            let loaded =
                { foreground_window = export<Window> library "rcf_mac_foreground_window"
                  view_window = export<ViewWindow> library "rcf_mac_view_window"
                  window_from_handle = export<NativeWindow> library "rcf_mac_window_from_handle"
                  begin_monitor = export<Begin> library "rcf_mac_monitor_begin"
                  end_monitor = export<End> library "rcf_mac_monitor_end"
                  monitor_window = export<SetWindow> library "rcf_mac_monitor_window"
                  raw_begin = export<RawBegin> library "rcf_mac_raw_begin"
                  raw_end = export<RawEnd> library "rcf_mac_raw_end"
                  raw_available = export<Size> library "rcf_mac_raw_available"
                  raw_validate = export<Size> library "rcf_mac_raw_validate"
                  raw_state = export<Size> library "rcf_mac_raw_state"
                  raw_drain = export<Size> library "rcf_mac_raw_drain"
                  raw_pending = export<Size> library "rcf_mac_raw_pending"
                  raw_boundary = export<Time> library "rcf_mac_raw_boundary"
                  raw_discard = export<Discard> library "rcf_mac_raw_discard"
                  raw_reconcile = export<Reconcile> library "rcf_mac_raw_reconcile"
                  raw_initial_key = export<InitialKey> library "rcf_mac_raw_initial_key"
                  raw_error = export<Size> library "rcf_mac_raw_error"
                  raw_started_at = export<Time> library "rcf_mac_raw_started_at"
                  raw_activate = export<Activate> library "rcf_mac_raw_activate"
                  raw_finish = export<Finish> library "rcf_mac_raw_finish"
                  raw_revoke = export<Discard> library "rcf_mac_raw_revoke"
                  entry_context = export<EntryContext> library "rcf_mac_entry_context"
                  raw_diagnostics = export<Diagnostics> library "rcf_mac_raw_diagnostics"
                  raw_guards_pending = export<Size> library "rcf_mac_raw_guards_pending"
                  uptime = export<Time> library "rcf_mac_uptime"
                  keyboard_boundary = export<Time> library "rcf_mac_keyboard_boundary"
                  capture_key = export<Size> library "rcf_mac_capture_key"
                  capture_button = export<Size> library "rcf_mac_capture_button" }

            // AppKit retains the callback until ownership cleanup removes the monitor.
            api <- Some loaded
            loaded
        with _ ->
            NativeLibrary.Free library
            reraise ()

let foreground_window () = (load ()).foreground_window.Invoke()

let view_window (view_serial_number: uint32) =
    (load ()).view_window.Invoke view_serial_number

let key_down (code: int) = CGEventSourceKeyState(0, uint16 code)

let mouse_down (button: int) =
    CGEventSourceButtonState(0, uint32 button)

let cursor_position () =
    let event = CGEventCreate 0n

    if event = 0n then
        failwith "CoreGraphics could not read the cursor position."

    try
        CGEventGetLocation event
    finally
        CFRelease event

let wheel_points_per_line () =
    let source = CGEventSourceCreate 0

    if source = 0n then
        failwith "CoreGraphics could not read the scroll scale."

    try
        let scale = CGEventSourceGetPixelsPerLine source

        if not (Double.IsFinite scale) || scale <= 0. then
            failwith "CoreGraphics returned an invalid scroll scale."

        scale
    finally
        CFRelease source
