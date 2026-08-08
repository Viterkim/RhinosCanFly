module RhinosCanFly.Platform.Win.Win32

open System
open System.ComponentModel
open System.Drawing
open System.Runtime.InteropServices

[<Literal>]
let WM_MOUSELEAVE = 0x02A3

[<Literal>]
let INPUT_MOUSE = 0u

[<Literal>]
let INPUT_KEYBOARD = 1u

[<Literal>]
let MOUSEEVENTF_MIDDLEDOWN = 0x00000020u

[<Literal>]
let MOUSEEVENTF_MIDDLEUP = 0x00000040u

[<Literal>]
let KEYEVENTF_KEYUP = 0x0002u

[<Literal>]
let WHEEL_DELTA = 120

[<Literal>]
let GA_ROOT = 2u

[<Literal>]
let QS_ALLINPUT = 0x04FFu

[<Literal>]
let MWMO_INPUTAVAILABLE = 0x0004u

[<Literal>]
let WAIT_FAILED = 0xFFFFFFFFu

[<Literal>]
let INFINITE = 0xFFFFFFFFu

[<Literal>]
let VK_LBUTTON = 0x01

[<Literal>]
let VK_RBUTTON = 0x02

[<Literal>]
let VK_MBUTTON = 0x04

[<Literal>]
let VK_XBUTTON1 = 0x05

[<Literal>]
let VK_XBUTTON2 = 0x06

[<Literal>]
let VK_SHIFT = 0x10

[<Literal>]
let VK_CONTROL = 0x11

[<Literal>]
let VK_MENU = 0x12

[<Literal>]
let VK_LSHIFT = 0xA0

[<Literal>]
let VK_RSHIFT = 0xA1

[<Literal>]
let VK_LCONTROL = 0xA2

[<Literal>]
let VK_RCONTROL = 0xA3

[<Literal>]
let VK_LMENU = 0xA4

[<Literal>]
let VK_RMENU = 0xA5

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
type MouseInput =
    val mutable dx: int
    val mutable dy: int
    val mutable mouse_data: uint32
    val mutable flags: uint32
    val mutable time: uint32
    val mutable extra_info: unativeint

[<Struct; StructLayout(LayoutKind.Sequential)>]
type NativeInput =
    val mutable input_type: uint32
    val mutable mouse: MouseInput

[<Struct; StructLayout(LayoutKind.Sequential)>]
type KeyboardInput =
    val mutable virtual_key: uint16
    val mutable scan_code: uint16
    val mutable flags: uint32
    val mutable time: uint32
    val mutable extra_info: unativeint

[<Struct; StructLayout(LayoutKind.Sequential)>]
type NativeKeyboardInput =
    val mutable input_type: uint32
    val mutable keyboard: KeyboardInput

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

[<DllImport("user32.dll", SetLastError = true)>]
extern uint32 SendInput(uint32 input_count, NativeInput[] inputs, int input_size)

[<DllImport("user32.dll", EntryPoint = "SendInput", SetLastError = true)>]
extern uint32 SendKeyboardInput(uint32 input_count, NativeKeyboardInput[] inputs, int input_size)

[<DllImport("user32.dll", SetLastError = true)>]
extern bool InvalidateRect(nativeint window, nativeint rectangle, bool erase)

[<DllImport("user32.dll", SetLastError = true)>]
extern bool UpdateWindow(nativeint window)

[<DllImport("user32.dll", SetLastError = true)>]
extern uint32 MsgWaitForMultipleObjectsEx(
    uint32 object_count,
    nativeint handles,
    uint32 milliseconds,
    uint32 wake_mask,
    uint32 flags
)

let win32_error (operation: string) (errorCode: int) =
    Win32Exception(errorCode)
    |> fun (error: Win32Exception) -> $"{operation} failed: {error.Message}"

let last_error (operation: string) =
    win32_error operation (Marshal.GetLastWin32Error())

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

let update_window (window: nativeint) =
    if UpdateWindow window then
        Ok()
    else
        Error(last_error "UpdateWindow")

let redraw_window (window: nativeint) =
    if not (InvalidateRect(window, nativeint 0, false)) then
        Error(last_error "InvalidateRect")
    else
        update_window window

let wait_for_input (timeoutMilliseconds: uint32) =
    let result =
        MsgWaitForMultipleObjectsEx(0u, nativeint 0, timeoutMilliseconds, QS_ALLINPUT, MWMO_INPUTAVAILABLE)

    if result = WAIT_FAILED then
        Error(last_error "MsgWaitForMultipleObjectsEx")
    else
        Ok()

let send_middle_mouse (down: bool) =
    let mutable mouse = Unchecked.defaultof<MouseInput>

    mouse.flags <-
        if down then
            MOUSEEVENTF_MIDDLEDOWN
        else
            MOUSEEVENTF_MIDDLEUP

    let mutable input = Unchecked.defaultof<NativeInput>
    input.input_type <- INPUT_MOUSE
    input.mouse <- mouse

    if SendInput(1u, [| input |], Marshal.SizeOf<NativeInput>()) = 1u then
        Ok()
    else
        Error(last_error "SendInput(middle mouse)")

let send_shift_key (down: bool) =
    let mutable keyboard = Unchecked.defaultof<KeyboardInput>
    keyboard.virtual_key <- uint16 VK_SHIFT
    keyboard.flags <- if down then 0u else KEYEVENTF_KEYUP

    let mutable input = Unchecked.defaultof<NativeKeyboardInput>
    input.input_type <- INPUT_KEYBOARD
    input.keyboard <- keyboard

    if SendKeyboardInput(1u, [| input |], Marshal.SizeOf<NativeKeyboardInput>()) = 1u then
        Ok()
    else
        Error(last_error "SendInput(shift)")
