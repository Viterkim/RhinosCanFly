module RhinosCanFly.Platform.Win.RawInputNative

#nowarn "9"

open System
open System.Runtime.InteropServices
open System.Threading
open Microsoft.Win32.SafeHandles
open Microsoft.FSharp.NativeInterop

[<Literal>]
let MESSAGE = 0x00FF

[<Literal>]
let WM_APP = 0x8000

[<Literal>]
let CONTROL_MESSAGE = WM_APP + 1

[<Literal>]
let REGISTRATION_RETRY_INTERVAL_MS = 100

[<Literal>]
let MESSAGE_ONLY_WINDOW = -3

[<Literal>]
let RID_INPUT = 0x10000003u

[<Literal>]
let RIM_TYPE_MOUSE = 0u

[<Literal>]
let RIDEV_REMOVE = 0x00000001u

[<Literal>]
let RIDEV_NO_LEGACY = 0x00000030u

[<Literal>]
let ERROR_INSUFFICIENT_BUFFER = 122

[<Literal>]
let MOUSE_MOVE_ABSOLUTE = 0x0001us

[<Literal>]
let LEFT_BUTTON_DOWN = 0x0001us

[<Literal>]
let LEFT_BUTTON_UP = 0x0002us

[<Literal>]
let RIGHT_BUTTON_DOWN = 0x0004us

[<Literal>]
let RIGHT_BUTTON_UP = 0x0008us

[<Literal>]
let MIDDLE_BUTTON_DOWN = 0x0010us

[<Literal>]
let MIDDLE_BUTTON_UP = 0x0020us

[<Literal>]
let BUTTON_4_DOWN = 0x0040us

[<Literal>]
let BUTTON_4_UP = 0x0080us

[<Literal>]
let BUTTON_5_DOWN = 0x0100us

[<Literal>]
let BUTTON_5_UP = 0x0200us

[<Literal>]
let MOUSE_WHEEL = 0x0400us

[<Literal>]
let GENERIC_DESKTOP_USAGE_PAGE = 0x01us

[<Literal>]
let MOUSE_USAGE = 0x02us

[<Literal>]
let BUTTON_WORD_MASK = 0xFFFFu

[<Literal>]
let SIGNED_WORD_THRESHOLD = 0x8000

[<Literal>]
let UNSIGNED_WORD_RANGE = 0x10000

[<Struct; StructLayout(LayoutKind.Sequential)>]
type Device =
    val mutable usage_page: uint16
    val mutable usage: uint16
    val mutable flags: uint32
    val mutable target: nativeint

type MouseRegistrationLease =
    { previous: Device option
      installed: Device
      mutable relinquished: bool
      mutable previous_registration_lost: bool }

type RegistrationRelease =
    | RestoredPrevious
    | AlreadyRelinquished
    | ReplacedByAnotherOwner
    | OwnRegistrationRemovedButPreviousRegistrationLost

type RegistrationAcquisition =
    | Acquired of MouseRegistrationLease
    | Failed of string
    | CleanupPending of string * MouseRegistrationLease

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
extern uint32 GetRawInputBuffer(nativeint data, uint32& size, uint32 header_size)

let device_size = uint32 (Marshal.SizeOf<Device>())
let header_size = uint32 (Marshal.SizeOf<Header>())
let mouse_input_size = header_size + uint32 (Marshal.SizeOf<Mouse>())

[<Literal>]
let RAW_INPUT_ALIGNMENT = 8

[<Literal>]
let INITIAL_BUFFER_PACKET_CAPACITY = 64

type HGlobalHandle(pointer: nativeint) =
    inherit SafeHandleZeroOrMinusOneIsInvalid(true)

    do base.SetHandle pointer

    override _.ReleaseHandle() =
        Marshal.FreeHGlobal pointer
        true

type InputBuffer(initial_capacity: int) =
    let aligned_pointer (allocation: HGlobalHandle) =
        let address = allocation.DangerousGetHandle().ToInt64()

        let aligned_address =
            (address + int64 (RAW_INPUT_ALIGNMENT - 1))
            &&& int64 (~~~(RAW_INPUT_ALIGNMENT - 1))

        nativeint aligned_address

    let allocate (capacity: int) =
        if capacity <= 0 || capacity > Int32.MaxValue - (RAW_INPUT_ALIGNMENT - 1) then
            invalidArg (nameof capacity) "The raw-input buffer capacity is invalid."

        new HGlobalHandle(Marshal.AllocHGlobal(capacity + RAW_INPUT_ALIGNMENT - 1))

    let mutable capacity = initial_capacity
    let mutable allocation = allocate initial_capacity
    let mutable pointer = aligned_pointer allocation
    let mutable disposed = false

    do
        if pointer.ToInt64() &&& int64 (RAW_INPUT_ALIGNMENT - 1) <> 0L then
            invalidOp "The raw-input buffer is not correctly aligned."

    member _.Capacity = capacity
    member _.Pointer = pointer

    member _.EnsureCapacity(required_capacity: int) =
        if disposed then
            ObjectDisposedException(nameof InputBuffer) |> raise

        if required_capacity > capacity then
            let replacement = allocate required_capacity
            let replacement_pointer = aligned_pointer replacement

            if replacement_pointer.ToInt64() &&& int64 (RAW_INPUT_ALIGNMENT - 1) <> 0L then
                replacement.Dispose()
                invalidOp "The replacement raw-input buffer is not correctly aligned."

            allocation.Dispose()
            allocation <- replacement
            pointer <- replacement_pointer
            capacity <- required_capacity

    member _.Dispose() =
        if not disposed then
            disposed <- true
            allocation.Dispose()
            pointer <- nativeint 0
            capacity <- 0

    interface IDisposable with
        member this.Dispose() = this.Dispose()

[<RequireQualifiedAccess>]
type MouseReadResult =
    | Failed = -3
    | BufferTooSmall = -2
    | Malformed = -1
    | Ignored = 0
    | Mouse = 1

let initial_input_buffer_capacity =
    int mouse_input_size * INITIAL_BUFFER_PACKET_CAPACITY

let same_device (left: Device) (right: Device) =
    left.usage_page = right.usage_page
    && left.usage = right.usage
    && left.flags = right.flags
    && left.target = right.target

let same_registration (left: Device option) (right: Device option) =
    match left with
    | None -> Option.isNone right
    | Some left_device ->
        match right with
        | Some right_device -> same_device left_device right_device
        | None -> false

[<Literal>]
let REGISTRATION_QUERY_ATTEMPTS = 3

let rec get_registered_mouse_attempt (attempt: int) =
    let mutable device_count = 0u

    let sizing_result =
        GetRegisteredRawInputDevices(nativeint 0, &device_count, device_size)

    let sizing_error = Marshal.GetLastWin32Error()

    if sizing_result = UInt32.MaxValue && sizing_error <> ERROR_INSUFFICIENT_BUFFER then
        Error(Win32.win32_error "GetRegisteredRawInputDevices" sizing_error)
    elif device_count = 0u then
        Ok None
    else
        let buffer = Marshal.AllocHGlobal(int (device_count * device_size))

        try
            let mutable capacity = device_count
            let read = GetRegisteredRawInputDevices(buffer, &capacity, device_size)

            if read = UInt32.MaxValue then
                let error = Marshal.GetLastWin32Error()

                if error = ERROR_INSUFFICIENT_BUFFER && attempt < REGISTRATION_QUERY_ATTEMPTS then
                    get_registered_mouse_attempt (attempt + 1)
                else
                    Error(Win32.win32_error "GetRegisteredRawInputDevices" error)
            else
                let count = min read device_count

                let mutable index = 0u
                let mutable registered_mouse = None

                while index < count && Option.isNone registered_mouse do
                    let address = IntPtr.Add(buffer, int (index * device_size))
                    let device = NativePtr.read (NativePtr.ofNativeInt<Device> address)

                    if device.usage_page = GENERIC_DESKTOP_USAGE_PAGE && device.usage = MOUSE_USAGE then
                        registered_mouse <- Some device

                    index <- index + 1u

                Ok registered_mouse
        finally
            Marshal.FreeHGlobal buffer

let get_registered_mouse () = get_registered_mouse_attempt 1

let mouse_registration_is_current (lease: MouseRegistrationLease) =
    if lease.relinquished then
        Ok false
    else
        match get_registered_mouse () with
        | Ok(Some current) -> Ok(same_device current lease.installed)
        | Ok None -> Ok false
        | Error error -> Error error

let mouse_device (target: nativeint) =
    let mutable device = Unchecked.defaultof<Device>
    device.usage_page <- GENERIC_DESKTOP_USAGE_PAGE
    device.usage <- MOUSE_USAGE
    device.flags <- RIDEV_NO_LEGACY
    device.target <- target
    device

let register_mouse (target: nativeint) =
    let mutable device = mouse_device target

    if RegisterRawInputDevices(&device, 1u, device_size) then
        Ok()
    else
        Error(Win32.last_error "RegisterRawInputDevices")

let unregister_mouse () =
    let mutable device = Unchecked.defaultof<Device>
    device.usage_page <- GENERIC_DESKTOP_USAGE_PAGE
    device.usage <- MOUSE_USAGE
    device.flags <- RIDEV_REMOVE
    device.target <- nativeint 0

    if RegisterRawInputDevices(&device, 1u, device_size) then
        Ok()
    else
        Error(Win32.last_error "RegisterRawInputDevices(remove)")

let restore_mouse (previous: Device option) =
    match previous with
    | Some previous ->
        let mutable device = previous

        if RegisterRawInputDevices(&device, 1u, device_size) then
            Ok()
        else
            Error(Win32.last_error "RegisterRawInputDevices(restore)")
    | None -> unregister_mouse ()

let rec acquire_mouse_registration (target: nativeint) =
    match get_registered_mouse () with
    | Error error -> Failed error
    | Ok previous ->
        let installed = mouse_device target

        match register_mouse target with
        | Error error -> Failed error
        | Ok() ->
            let lease =
                { previous = previous
                  installed = installed
                  relinquished = false
                  previous_registration_lost = false }

            let verification = get_registered_mouse ()

            match verification with
            | Ok current when same_registration current (Some installed) -> Acquired lease
            | _ ->
                let verification_error =
                    match verification with
                    | Ok _ -> "The raw-mouse registration changed before it could be verified."
                    | Error error -> $"The raw-mouse registration could not be verified: {error}"

                match release_mouse_registration lease with
                | Ok OwnRegistrationRemovedButPreviousRegistrationLost ->
                    CleanupPending(
                        $"{verification_error}; RhinosCanFly removed its registration but could not restore the previous raw-mouse owner.",
                        lease
                    )
                | Ok _ -> Failed verification_error
                | Error release_error -> CleanupPending($"{verification_error}; cleanup failed: {release_error}", lease)

and release_mouse_registration (lease: MouseRegistrationLease) =
    if lease.relinquished then
        if lease.previous_registration_lost then
            match get_registered_mouse () with
            | Ok current when same_registration current lease.previous ->
                lease.previous_registration_lost <- false
                Ok RestoredPrevious
            | Ok _
            | Error _ -> Ok OwnRegistrationRemovedButPreviousRegistrationLost
        else
            Ok AlreadyRelinquished
    else
        match get_registered_mouse () with
        | Error error -> Error error
        | Ok current when not (same_registration current (Some lease.installed)) ->
            lease.relinquished <- true

            if lease.previous_registration_lost then
                if same_registration current lease.previous then
                    lease.previous_registration_lost <- false
                    Ok RestoredPrevious
                else
                    Ok OwnRegistrationRemovedButPreviousRegistrationLost
            else
                Ok ReplacedByAnotherOwner
        | Ok _ ->
            let mutable attempt = 1
            let mutable release_error = None
            let mutable release = RestoredPrevious

            while attempt <= REGISTRATION_QUERY_ATTEMPTS && not lease.relinquished do
                match restore_mouse lease.previous with
                | Ok() ->
                    match get_registered_mouse () with
                    | Ok current when same_registration current lease.previous ->
                        lease.relinquished <- true
                        lease.previous_registration_lost <- false
                    | Ok current when not (same_registration current (Some lease.installed)) ->
                        lease.relinquished <- true
                        release <- ReplacedByAnotherOwner
                    | Ok _ -> release_error <- Some "The raw-mouse registration still belongs to RhinosCanFly."
                    | Error error -> release_error <- Some error
                | Error error ->
                    release_error <- Some error

                    match get_registered_mouse () with
                    | Ok current when not (same_registration current (Some lease.installed)) ->
                        lease.relinquished <- true

                        release <-
                            if same_registration current lease.previous then
                                RestoredPrevious
                            else
                                ReplacedByAnotherOwner
                    | Ok _ -> ()
                    | Error query_error -> release_error <- Some $"{error}; {query_error}"

                attempt <- attempt + 1

                if not lease.relinquished && attempt <= REGISTRATION_QUERY_ATTEMPTS then
                    Thread.Yield() |> ignore

            if not lease.relinquished then
                match get_registered_mouse () with
                | Ok current when not (same_registration current (Some lease.installed)) ->
                    lease.relinquished <- true

                    release <-
                        if same_registration current lease.previous then
                            lease.previous_registration_lost <- false
                            RestoredPrevious
                        else
                            ReplacedByAnotherOwner
                | Ok _ ->
                    match unregister_mouse () with
                    | Error error -> release_error <- Some $"{release_error |> Option.defaultValue error}; {error}"
                    | Ok() ->
                        lease.relinquished <- true
                        lease.previous_registration_lost <- Option.isSome lease.previous

                        match get_registered_mouse () with
                        | Ok current when same_registration current lease.previous ->
                            lease.previous_registration_lost <- false
                            release <- RestoredPrevious
                        | Ok current when same_registration current (Some lease.installed) ->
                            lease.relinquished <- false
                            release_error <- Some "Emergency raw-mouse removal did not relinquish ownership."
                        | Ok _ when not lease.previous_registration_lost -> release <- ReplacedByAnotherOwner
                        | Ok _ -> ()
                        | Error error -> release_error <- Some error
                | Error error -> release_error <- Some error

            if lease.relinquished then
                if lease.previous_registration_lost then
                    Ok OwnRegistrationRemovedButPreviousRegistrationLost
                else
                    Ok release
            else
                Error(
                    release_error
                    |> Option.defaultValue "The raw-mouse registration could not be relinquished."
                )

let button_flags (mouse: Mouse) =
    uint16 (mouse.buttons &&& BUTTON_WORD_MASK)

let button_data (mouse: Mouse) =
    uint16 (mouse.buttons >>> 16 &&& BUTTON_WORD_MASK)

let signed_button_data (mouse: Mouse) =
    let value = int (button_data mouse)

    if value >= SIGNED_WORD_THRESHOLD then
        value - UNSIGNED_WORD_RANGE
    else
        value

let decode_mouse (buffer: nativeint) (available_bytes: int) (record_size: byref<uint32>) (mouse: byref<Mouse>) =
    if available_bytes < int header_size then
        MouseReadResult.Malformed
    else
        let header = NativePtr.read (NativePtr.ofNativeInt<Header> buffer)
        record_size <- header.size

        if header.size < header_size || header.size > uint32 available_bytes then
            MouseReadResult.Malformed
        elif header.input_type <> RIM_TYPE_MOUSE then
            MouseReadResult.Ignored
        elif header.size < mouse_input_size then
            MouseReadResult.Malformed
        else
            let mouse_buffer = IntPtr.Add(buffer, int header_size)
            mouse <- NativePtr.read (NativePtr.ofNativeInt<Mouse> mouse_buffer)
            MouseReadResult.Mouse

let read_current_mouse
    (raw_input: nativeint)
    (buffer: InputBuffer)
    (required_bytes: byref<uint32>)
    (error_code: byref<int>)
    (mouse: byref<Mouse>)
    =
    let mutable bytes = uint32 buffer.Capacity

    let bytes_read =
        GetRawInputData(raw_input, RID_INPUT, buffer.Pointer, &bytes, header_size)

    required_bytes <- bytes

    if bytes_read = UInt32.MaxValue then
        error_code <- Marshal.GetLastWin32Error()

        if error_code = ERROR_INSUFFICIENT_BUFFER then
            MouseReadResult.BufferTooSmall
        else
            MouseReadResult.Failed
    elif bytes_read > uint32 buffer.Capacity then
        MouseReadResult.Malformed
    else
        let mutable record_size = 0u
        decode_mouse buffer.Pointer (int bytes_read) &record_size &mouse

let read_buffered (buffer: InputBuffer) (bytes: byref<uint32>) (error_code: byref<int>) =
    bytes <- uint32 buffer.Capacity
    let count = GetRawInputBuffer(buffer.Pointer, &bytes, header_size)

    if count = UInt32.MaxValue then
        error_code <- Marshal.GetLastWin32Error()

    count

let aligned_record_size (record_size: uint32) =
    let mask = uint64 (RAW_INPUT_ALIGNMENT - 1)
    let aligned = (uint64 record_size + mask) &&& (~~~mask)

    if aligned > uint64 Int32.MaxValue then -1 else int aligned

let post_control (window: nativeint) =
    if Win32Native.PostMessage(window, CONTROL_MESSAGE, nativeint 0, nativeint 0) then
        Ok()
    else
        Error(Win32.last_error "PostMessage")
