module RhinosCanFly.Platform.Mac.MacNative

open System
open System.IO
open System.Runtime.InteropServices
open RhinosCanFly

[<Literal>]
let BRIDGE_ABI = 10u

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
type RawBegin = delegate of RelativeMotion.Handler -> int32

[<UnmanagedFunctionPointer(CallingConvention.Cdecl)>]
type RawEnd = delegate of unit -> int32

[<UnmanagedFunctionPointer(CallingConvention.Cdecl)>]
type Begin = delegate of Handler * nativeint -> int32

[<UnmanagedFunctionPointer(CallingConvention.Cdecl)>]
type End = delegate of unit -> int32

[<UnmanagedFunctionPointer(CallingConvention.Cdecl)>]
type Size = delegate of unit -> uint32

[<UnmanagedFunctionPointer(CallingConvention.Cdecl)>]
type Count = delegate of unit -> uint64

[<UnmanagedFunctionPointer(CallingConvention.Cdecl)>]
type Time = delegate of unit -> float

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
      raw_discovered: Size
      raw_rejected: Size
      raw_motion_count: Count
      uptime: Time
      keyboard_boundary: Time
      capture_key: Size
      capture_button: Size }

let mutable api: Api option = None

let export<'t when 't :> Delegate> (library: nativeint) (name: string) =
    Marshal.GetDelegateForFunctionPointer<'t>(NativeLibrary.GetExport(library, name))

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
            if
                (export<Size> library "rcf_mac_abi").Invoke() <> BRIDGE_ABI
                || (export<Size> library "rcf_mac_event_size").Invoke()
                   <> uint32 (Marshal.SizeOf<InputEvent>())
                || (export<Size> library "rcf_mac_motion_size").Invoke()
                   <> uint32 (Marshal.SizeOf<RelativeMotion.Packet>())
            then
                invalidOp "The Mac input bridge does not match this build."

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
                  raw_discovered = export<Size> library "rcf_mac_raw_discovered"
                  raw_rejected = export<Size> library "rcf_mac_raw_rejected"
                  raw_motion_count = export<Count> library "rcf_mac_raw_motion_count"
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
