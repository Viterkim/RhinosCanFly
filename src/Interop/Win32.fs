module RhinosCanFly.Win32

open System
open System.ComponentModel
open System.Drawing
open System.Runtime.InteropServices

[<Literal>]
let WM_INPUT = 0x00FF

[<Literal>]
let WM_KEYDOWN = 0x0100

[<Literal>]
let WM_KEYUP = 0x0101

[<Literal>]
let WM_CHAR = 0x0102

[<Literal>]
let WM_SYSKEYDOWN = 0x0104

[<Literal>]
let WM_SYSKEYUP = 0x0105

[<Literal>]
let WM_SYSCHAR = 0x0106

[<Literal>]
let WM_MOUSELEAVE = 0x02A3

[<Literal>]
let RID_INPUT = 0x10000003u

[<Literal>]
let RIM_TYPEMOUSE = 0u

[<Literal>]
let RIDEV_REMOVE = 0x00000001u

[<Literal>]
let RIDEV_NOLEGACY = 0x00000030u

[<Literal>]
let ERROR_INSUFFICIENT_BUFFER = 122

[<Literal>]
let MOUSE_MOVE_ABSOLUTE = 0x0001us

[<Literal>]
let RI_MOUSE_LEFT_BUTTON_DOWN = 0x0001us

[<Literal>]
let RI_MOUSE_RIGHT_BUTTON_DOWN = 0x0004us

[<Literal>]
let RI_MOUSE_MIDDLE_BUTTON_DOWN = 0x0010us

[<Literal>]
let RI_MOUSE_WHEEL = 0x0400us

[<Literal>]
let WHEEL_DELTA = 120

[<Literal>]
let GA_ROOT = 2u

[<Struct; StructLayout(LayoutKind.Sequential)>]
type NativePoint =
    val mutable x: int
    val mutable y: int

[<Struct; StructLayout(LayoutKind.Sequential)>]
type NativeRect =
    val mutable left: int
    val mutable top: int
    val mutable right: int
    val mutable bottom: int

[<Struct; StructLayout(LayoutKind.Sequential)>]
type RawInputDevice =
    val mutable usage_page: uint16
    val mutable usage: uint16
    val mutable flags: uint32
    val mutable target: nativeint

[<Struct; StructLayout(LayoutKind.Sequential)>]
type RawInputHeader =
    val mutable input_type: uint32
    val mutable size: uint32
    val mutable device: nativeint
    val mutable wparam: nativeint

[<Struct; StructLayout(LayoutKind.Sequential)>]
type RawMouse =
    val mutable flags: uint16
    val mutable buttons: uint32
    val mutable raw_buttons: uint32
    val mutable last_x: int
    val mutable last_y: int
    val mutable extra_information: uint32

[<DllImport("user32.dll", SetLastError = true)>]
extern bool RegisterRawInputDevices(RawInputDevice[] devices, uint32 device_count, uint32 device_size)

[<DllImport("user32.dll", SetLastError = true)>]
extern uint32 GetRegisteredRawInputDevices(nativeint devices, uint32& device_count, uint32 device_size)

[<DllImport("user32.dll", SetLastError = true)>]
extern uint32 GetRawInputData(nativeint raw_input, uint32 command, nativeint data, uint32& size, uint32 header_size)

[<DllImport("user32.dll")>]
extern int16 GetAsyncKeyState(int virtual_key)

[<DllImport("user32.dll", SetLastError = true)>]
extern bool GetCursorPos(NativePoint& point)

[<DllImport("user32.dll", SetLastError = true)>]
extern bool SetCursorPos(int x, int y)

[<DllImport("user32.dll", EntryPoint = "ClipCursor", SetLastError = true)>]
extern bool ClipCursorRect(NativeRect& rectangle)

[<DllImport("user32.dll", EntryPoint = "ClipCursor", SetLastError = true)>]
extern bool ClipCursorClear(nativeint rectangle)

[<DllImport("user32.dll")>]
extern int ShowCursor(bool show)

[<DllImport("user32.dll")>]
extern nativeint SetFocus(nativeint window)

[<DllImport("user32.dll")>]
extern nativeint GetForegroundWindow()

[<DllImport("user32.dll")>]
extern nativeint GetAncestor(nativeint window, uint32 flags)

[<DllImport("user32.dll")>]
extern nativeint SendMessage(nativeint window, int message, nativeint wparam, nativeint lparam)

let win32_error (operation: string) (errorCode: int) =
    Win32Exception(errorCode)
    |> fun (error: Win32Exception) -> $"{operation} failed: {error.Message}"

let last_error (operation: string) =
    win32_error operation (Marshal.GetLastWin32Error())

let raw_input_device_size = uint32 (Marshal.SizeOf<RawInputDevice>())

let get_registered_raw_mouse () =
    let mutable deviceCount = 0u
    let sizingResult = GetRegisteredRawInputDevices(nativeint 0, &deviceCount, raw_input_device_size)
    let sizingError = Marshal.GetLastWin32Error()

    if sizingResult = UInt32.MaxValue && sizingError <> ERROR_INSUFFICIENT_BUFFER then
        Error(win32_error "GetRegisteredRawInputDevices" sizingError)
    elif deviceCount = 0u then
        Ok None
    else
        let buffer = Marshal.AllocHGlobal(int (deviceCount * raw_input_device_size))

        try
            let mutable capacity = deviceCount
            let read = GetRegisteredRawInputDevices(buffer, &capacity, raw_input_device_size)

            if read = UInt32.MaxValue then
                Error(last_error "GetRegisteredRawInputDevices")
            else
                let count = min read deviceCount

                if count = 0u then
                    Ok None
                else
                    seq { 0u .. count - 1u }
                    |> Seq.map (fun (index: uint32) ->
                        Marshal.PtrToStructure<RawInputDevice>(
                            IntPtr.Add(buffer, int (index * raw_input_device_size))
                        ))
                    |> Seq.tryFind (fun (device: RawInputDevice) ->
                        device.usage_page = 0x01us && device.usage = 0x02us)
                    |> Ok
        finally
            Marshal.FreeHGlobal buffer

let register_raw_mouse (target: nativeint) =
    let mutable device = Unchecked.defaultof<RawInputDevice>
    device.usage_page <- 0x01us
    device.usage <- 0x02us
    device.flags <- RIDEV_NOLEGACY
    device.target <- target

    if RegisterRawInputDevices([| device |], 1u, raw_input_device_size) then
        Ok()
    else
        Error(last_error "RegisterRawInputDevices")

let unregister_raw_mouse () =
    let mutable device = Unchecked.defaultof<RawInputDevice>
    device.usage_page <- 0x01us
    device.usage <- 0x02us
    device.flags <- RIDEV_REMOVE
    device.target <- nativeint 0

    if RegisterRawInputDevices([| device |], 1u, raw_input_device_size) then
        Ok()
    else
        Error(last_error "RegisterRawInputDevices(remove)")

let restore_raw_mouse (previous: RawInputDevice option) =
    match previous with
    | Some device ->
        if RegisterRawInputDevices([| device |], 1u, raw_input_device_size) then
            Ok()
        else
            Error(last_error "RegisterRawInputDevices(restore)")
    | None -> unregister_raw_mouse ()

let raw_button_flags (mouse: RawMouse) = uint16 (mouse.buttons &&& 0xFFFFu)

let raw_button_data (mouse: RawMouse) =
    uint16 (mouse.buttons >>> 16 &&& 0xFFFFu)

let signed_button_data (mouse: RawMouse) =
    let value = int (raw_button_data mouse)
    if value >= 0x8000 then value - 0x10000 else value

let try_read_raw_mouse (raw_input: nativeint) (buffer: nativeint) (buffer_capacity: int) =
    let header_size = uint32 (Marshal.SizeOf<RawInputHeader>())
    let mutable bytes = uint32 buffer_capacity
    let read = GetRawInputData(raw_input, RID_INPUT, buffer, &bytes, header_size)

    if read = UInt32.MaxValue || read = 0u then
        None
    else
        let header = Marshal.PtrToStructure<RawInputHeader> buffer

        if header.input_type <> RIM_TYPEMOUSE then
            None
        else
            Some(Marshal.PtrToStructure<RawMouse>(IntPtr.Add(buffer, int header_size)))

let get_cursor_position () =
    let mutable point = Unchecked.defaultof<NativePoint>

    if GetCursorPos(&point) then
        Ok(Point(point.x, point.y))
    else
        Error(last_error "GetCursorPos")

let set_cursor_position (point: Point) =
    if SetCursorPos(point.X, point.Y) then
        Ok()
    else
        Error(last_error "SetCursorPos")

let clip_cursor (rectangle: Rectangle) =
    let mutable native = Unchecked.defaultof<NativeRect>
    native.left <- rectangle.Left
    native.top <- rectangle.Top
    native.right <- rectangle.Right
    native.bottom <- rectangle.Bottom

    if ClipCursorRect(&native) then
        Ok()
    else
        Error(last_error "ClipCursor")

let clear_cursor_clip () =
    if ClipCursorClear(nativeint 0) then
        Ok()
    else
        Error(last_error "ClipCursor(null)")

let clear_mouse_hover (window: nativeint) =
    SendMessage(window, WM_MOUSELEAVE, nativeint 0, nativeint 0) |> ignore
