module RhinosCanFly.Platform.Win.RawInputNative

#nowarn "9"

open System
open System.Runtime.InteropServices
open Microsoft.FSharp.NativeInterop

[<Literal>]
let message = 0x00FF

[<Literal>]
let wm_app = 0x8000

[<Literal>]
let stop_message = wm_app + 1

[<Literal>]
let quit_message = 0x0012

[<Literal>]
let message_only_window = -3

[<Literal>]
let rid_input = 0x10000003u

[<Literal>]
let rim_type_mouse = 0u

[<Literal>]
let ridev_remove = 0x00000001u

[<Literal>]
let ridev_no_legacy = 0x00000030u

[<Literal>]
let error_insufficient_buffer = 122

[<Literal>]
let mouse_move_absolute = 0x0001us

[<Literal>]
let left_button_down = 0x0001us

[<Literal>]
let right_button_down = 0x0004us

[<Literal>]
let right_button_up = 0x0008us

[<Literal>]
let middle_button_down = 0x0010us

[<Literal>]
let middle_button_up = 0x0020us

[<Literal>]
let button_4_down = 0x0040us

[<Literal>]
let button_4_up = 0x0080us

[<Literal>]
let button_5_down = 0x0100us

[<Literal>]
let button_5_up = 0x0200us

[<Literal>]
let mouse_wheel = 0x0400us

[<Literal>]
let generic_desktop_usage_page = 0x01us

[<Literal>]
let mouse_usage = 0x02us

[<Literal>]
let button_word_mask = 0xFFFFu

[<Literal>]
let signed_word_threshold = 0x8000

[<Literal>]
let unsigned_word_range = 0x10000

[<Struct; StructLayout(LayoutKind.Sequential)>]
type Device =
    val mutable usage_page: uint16
    val mutable usage: uint16
    val mutable flags: uint32
    val mutable target: nativeint

[<Struct; StructLayout(LayoutKind.Sequential)>]
type Header =
    val mutable input_type: uint32
    val mutable size: uint32
    val mutable device: nativeint
    val mutable wparam: nativeint

[<Struct; StructLayout(LayoutKind.Sequential)>]
type Mouse =
    val mutable flags: uint16
    val mutable buttons: uint32
    val mutable raw_buttons: uint32
    val mutable last_x: int
    val mutable last_y: int
    val mutable extra_information: uint32

[<DllImport("user32.dll", SetLastError = true)>]
extern bool RegisterRawInputDevices(Device[] devices, uint32 device_count, uint32 device_size)

[<DllImport("user32.dll", SetLastError = true)>]
extern uint32 GetRegisteredRawInputDevices(nativeint devices, uint32& device_count, uint32 device_size)

[<DllImport("user32.dll", SetLastError = true)>]
extern uint32 GetRawInputData(nativeint raw_input, uint32 command, nativeint data, uint32& size, uint32 header_size)

[<DllImport("user32.dll", SetLastError = true)>]
extern bool PostMessage(nativeint window, int message, nativeint wparam, nativeint lparam)

[<DllImport("user32.dll", SetLastError = true)>]
extern bool PostThreadMessage(uint32 thread_id, int message, nativeint wparam, nativeint lparam)

[<DllImport("kernel32.dll")>]
extern uint32 GetCurrentThreadId()

let deviceSize = uint32 (Marshal.SizeOf<Device>())
let headerSize = uint32 (Marshal.SizeOf<Header>())
let mouseInputSize = headerSize + uint32 (Marshal.SizeOf<Mouse>())

let get_registered_mouse () =
    let mutable deviceCount = 0u

    let sizingResult =
        GetRegisteredRawInputDevices(nativeint 0, &deviceCount, deviceSize)

    let sizingError = Marshal.GetLastWin32Error()

    if sizingResult = UInt32.MaxValue && sizingError <> error_insufficient_buffer then
        Error(Win32.win32_error "GetRegisteredRawInputDevices" sizingError)
    elif deviceCount = 0u then
        Ok None
    else
        let buffer = Marshal.AllocHGlobal(int (deviceCount * deviceSize))

        try
            let mutable capacity = deviceCount
            let read = GetRegisteredRawInputDevices(buffer, &capacity, deviceSize)

            if read = UInt32.MaxValue then
                Error(Win32.last_error "GetRegisteredRawInputDevices")
            else
                let count = min read deviceCount

                if count = 0u then
                    Ok None
                else
                    seq { 0u .. count - 1u }
                    |> Seq.map (fun (index: uint32) ->
                        Marshal.PtrToStructure<Device>(IntPtr.Add(buffer, int (index * deviceSize))))
                    |> Seq.tryFind (fun (device: Device) ->
                        device.usage_page = generic_desktop_usage_page && device.usage = mouse_usage)
                    |> Ok
        finally
            Marshal.FreeHGlobal buffer

let register_mouse (target: nativeint) =
    let mutable device = Unchecked.defaultof<Device>
    device.usage_page <- generic_desktop_usage_page
    device.usage <- mouse_usage
    device.flags <- ridev_no_legacy
    device.target <- target

    if RegisterRawInputDevices([| device |], 1u, deviceSize) then
        Ok()
    else
        Error(Win32.last_error "RegisterRawInputDevices")

let unregister_mouse () =
    let mutable device = Unchecked.defaultof<Device>
    device.usage_page <- generic_desktop_usage_page
    device.usage <- mouse_usage
    device.flags <- ridev_remove
    device.target <- nativeint 0

    if RegisterRawInputDevices([| device |], 1u, deviceSize) then
        Ok()
    else
        Error(Win32.last_error "RegisterRawInputDevices(remove)")

let restore_mouse (previous: Device option) =
    match previous with
    | Some device ->
        if RegisterRawInputDevices([| device |], 1u, deviceSize) then
            Ok()
        else
            Error(Win32.last_error "RegisterRawInputDevices(restore)")
    | None -> unregister_mouse ()

let button_flags (mouse: Mouse) =
    uint16 (mouse.buttons &&& button_word_mask)

let button_data (mouse: Mouse) =
    uint16 (mouse.buttons >>> 16 &&& button_word_mask)

let signed_button_data (mouse: Mouse) =
    let value = int (button_data mouse)

    if value >= signed_word_threshold then
        value - unsigned_word_range
    else
        value

let try_read_mouse (rawInput: nativeint) (buffer: nativeint) (bufferCapacity: int) (mouse: byref<Mouse>) =
    let mutable bytes = uint32 bufferCapacity
    let bytesRead = GetRawInputData(rawInput, rid_input, buffer, &bytes, headerSize)

    if bytesRead = UInt32.MaxValue || bytesRead < headerSize then
        false
    else
        let header = NativePtr.read (NativePtr.ofNativeInt<Header> buffer)

        if
            header.input_type <> rim_type_mouse
            || header.size < mouseInputSize
            || bytesRead < mouseInputSize
        then
            false
        else
            let mouseBuffer = IntPtr.Add(buffer, int headerSize)
            mouse <- NativePtr.read (NativePtr.ofNativeInt<Mouse> mouseBuffer)
            true

let post_stop (window: nativeint) =
    if PostMessage(window, stop_message, nativeint 0, nativeint 0) then
        Ok()
    else
        Error(Win32.last_error "PostMessage")

let post_quit (threadId: uint32) =
    if PostThreadMessage(threadId, quit_message, nativeint 0, nativeint 0) then
        Ok()
    else
        Error(Win32.last_error "PostThreadMessage")
