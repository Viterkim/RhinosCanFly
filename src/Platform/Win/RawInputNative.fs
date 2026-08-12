module RhinosCanFly.Platform.Win.RawInputNative

#nowarn "9"

open System
open System.Runtime.InteropServices
open System.Threading
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

type MouseRegistrationLease =
    { gate: obj
      previous: Device option
      installed: Device
      mutable relinquished: bool }

type RegistrationRelease =
    | Relinquished
    | AlreadyRelinquished
    | ReplacedByAnotherOwner

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
extern bool RegisterRawInputDevices(Device& devices, uint32 device_count, uint32 device_size)

[<DllImport("user32.dll", SetLastError = true)>]
extern uint32 GetRegisteredRawInputDevices(nativeint devices, uint32& device_count, uint32 device_size)

[<DllImport("user32.dll", SetLastError = true)>]
extern uint32 GetRawInputData(nativeint raw_input, uint32 command, nativeint data, uint32& size, uint32 header_size)

[<DllImport("user32.dll", SetLastError = true)>]
extern bool PostThreadMessage(uint32 thread_id, int message, nativeint wparam, nativeint lparam)

[<DllImport("kernel32.dll")>]
extern uint32 GetCurrentThreadId()

let deviceSize = uint32 (Marshal.SizeOf<Device>())
let headerSize = uint32 (Marshal.SizeOf<Header>())
let mouseInputSize = headerSize + uint32 (Marshal.SizeOf<Mouse>())

let same_device (left: Device) (right: Device) =
    left.usage_page = right.usage_page
    && left.usage = right.usage
    && left.flags = right.flags
    && left.target = right.target

let same_registration (left: Device option) (right: Device option) =
    match left, right with
    | None, None -> true
    | Some leftDevice, Some rightDevice -> same_device leftDevice rightDevice
    | Some _, None
    | None, Some _ -> false

[<Literal>]
let registration_query_attempts = 3

let rec get_registered_mouse_attempt (attempt: int) =
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
                let error = Marshal.GetLastWin32Error()

                if error = error_insufficient_buffer && attempt < registration_query_attempts then
                    get_registered_mouse_attempt (attempt + 1)
                else
                    Error(Win32.win32_error "GetRegisteredRawInputDevices" error)
            else
                let count = min read deviceCount

                let mutable index = 0u
                let mutable registeredMouse = None

                while index < count && Option.isNone registeredMouse do
                    let address = IntPtr.Add(buffer, int (index * deviceSize))
                    let device = NativePtr.read (NativePtr.ofNativeInt<Device> address)

                    if device.usage_page = generic_desktop_usage_page && device.usage = mouse_usage then
                        registeredMouse <- Some device

                    index <- index + 1u

                Ok registeredMouse
        finally
            Marshal.FreeHGlobal buffer

let get_registered_mouse () = get_registered_mouse_attempt 1

let mouse_device (target: nativeint) =
    let mutable device = Unchecked.defaultof<Device>
    device.usage_page <- generic_desktop_usage_page
    device.usage <- mouse_usage
    device.flags <- ridev_no_legacy
    device.target <- target
    device

let register_mouse (target: nativeint) =
    let mutable device = mouse_device target

    if RegisterRawInputDevices(&device, 1u, deviceSize) then
        Ok()
    else
        Error(Win32.last_error "RegisterRawInputDevices")

let unregister_mouse () =
    let mutable device = Unchecked.defaultof<Device>
    device.usage_page <- generic_desktop_usage_page
    device.usage <- mouse_usage
    device.flags <- ridev_remove
    device.target <- nativeint 0

    if RegisterRawInputDevices(&device, 1u, deviceSize) then
        Ok()
    else
        Error(Win32.last_error "RegisterRawInputDevices(remove)")

let restore_mouse (previous: Device option) =
    match previous with
    | Some previous ->
        let mutable device = previous

        if RegisterRawInputDevices(&device, 1u, deviceSize) then
            Ok()
        else
            Error(Win32.last_error "RegisterRawInputDevices(restore)")
    | None -> unregister_mouse ()

let rec acquire_mouse_registration (target: nativeint) =
    match get_registered_mouse () with
    | Error error -> Error error
    | Ok previous ->
        let installed = mouse_device target

        match register_mouse target with
        | Error error -> Error error
        | Ok() ->
            let lease =
                { gate = obj ()
                  previous = previous
                  installed = installed
                  relinquished = false }

            let verification = get_registered_mouse ()

            match verification with
            | Ok current when same_registration current (Some installed) ->
                InputDiagnostics.record InputDiagnostics.EventKind.RegistrationAcquired (target.ToInt64()) 0L
                Ok lease
            | _ ->
                let verificationError =
                    match verification with
                    | Ok _ -> "The raw-mouse registration changed before it could be verified."
                    | Error error -> $"The raw-mouse registration could not be verified: {error}"

                match release_mouse_registration lease with
                | Ok _ -> Error verificationError
                | Error releaseError -> Error $"{verificationError}; cleanup failed: {releaseError}"

and release_mouse_registration (lease: MouseRegistrationLease) =
    lock lease.gate (fun () ->
        if lease.relinquished then
            Ok AlreadyRelinquished
        else
            match get_registered_mouse () with
            | Error error -> Error error
            | Ok current when not (same_registration current (Some lease.installed)) ->
                lease.relinquished <- true

                InputDiagnostics.record
                    InputDiagnostics.EventKind.RegistrationReplaced
                    (lease.installed.target.ToInt64())
                    0L

                Ok ReplacedByAnotherOwner
            | Ok _ ->
                let mutable attempt = 1
                let mutable releaseError = None

                while attempt <= registration_query_attempts && not lease.relinquished do
                    match restore_mouse lease.previous with
                    | Ok() ->
                        match get_registered_mouse () with
                        | Ok current when same_registration current lease.previous -> lease.relinquished <- true
                        | Ok current when not (same_registration current (Some lease.installed)) ->
                            lease.relinquished <- true
                        | Ok _ -> releaseError <- Some "The raw-mouse registration still belongs to RhinosCanFly."
                        | Error error -> releaseError <- Some error
                    | Error error ->
                        releaseError <- Some error

                        match get_registered_mouse () with
                        | Ok current when not (same_registration current (Some lease.installed)) ->
                            lease.relinquished <- true
                        | Ok _ -> ()
                        | Error queryError -> releaseError <- Some $"{error}; {queryError}"

                    attempt <- attempt + 1

                    if not lease.relinquished && attempt <= registration_query_attempts then
                        Thread.Yield() |> ignore

                if lease.relinquished then
                    InputDiagnostics.record
                        InputDiagnostics.EventKind.RegistrationReleased
                        (lease.installed.target.ToInt64())
                        0L

                    Ok Relinquished
                else
                    Error(
                        releaseError
                        |> Option.defaultValue "The raw-mouse registration could not be relinquished."
                    ))

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
    if Win32Native.PostMessage(window, stop_message, nativeint 0, nativeint 0) then
        Ok()
    else
        Error(Win32.last_error "PostMessage")

let post_quit (threadId: uint32) =
    if PostThreadMessage(threadId, quit_message, nativeint 0, nativeint 0) then
        Ok()
    else
        Error(Win32.last_error "PostThreadMessage")
