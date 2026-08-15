module RhinosCanFly.Platform.Win.Win32Native

#nowarn "9"

open System.Runtime.InteropServices
open System.Text

[<Literal>]
let WM_NULL = 0x0000

[<Literal>]
let WM_MOUSELEAVE = 0x02A3

[<Literal>]
let TTM_POP = 0x041C

[<Literal>]
let TOOLTIP_WINDOW_CLASS = "tooltips_class32"

[<Literal>]
let WHEEL_DELTA = 120

[<Literal>]
let GA_ROOT = 2u

[<Literal>]
let QS_ALLINPUT = 0x04FFu

[<Literal>]
let MWMO_INPUTAVAILABLE = 0x0004u

[<Literal>]
let RDW_INVALIDATE = 0x0001u

[<Literal>]
let RDW_ALLCHILDREN = 0x0080u

[<Literal>]
let RDW_FRAME = 0x0400u

[<Literal>]
let WAIT_FAILED = 0xFFFFFFFFu

[<Literal>]
let INFINITE = 0xFFFFFFFFu

[<Literal>]
let WH_KEYBOARD = 2

[<Literal>]
let WH_MOUSE = 7

[<Literal>]
let HC_ACTION = 0

[<Literal>]
let KEYBOARD_EXTENDED_KEY = 0x01000000L

[<Literal>]
let KEYBOARD_PREVIOUSLY_DOWN = 0x40000000L

[<Literal>]
let KEYBOARD_KEY_RELEASED = 0x80000000L

[<Literal>]
let KEYBOARD_SCAN_CODE_MASK = 0x00FF0000L

[<Literal>]
let KEYBOARD_SCAN_CODE_SHIFT = 16

[<Literal>]
let RIGHT_SHIFT_SCAN_CODE = 0x36

[<Literal>]
let WM_RBUTTONDOWN = 0x0204

[<Literal>]
let WM_RBUTTONUP = 0x0205

[<Literal>]
let WM_RBUTTONDBLCLK = 0x0206

[<Literal>]
let WM_XBUTTONDOWN = 0x020B

[<Literal>]
let WM_XBUTTONUP = 0x020C

[<Literal>]
let WM_XBUTTONDBLCLK = 0x020D

[<Literal>]
let XBUTTON1 = 0x0001u

[<Literal>]
let XBUTTON2 = 0x0002u

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
type MouseHookData =
    val mutable point: NativePoint
    val mutable window: nativeint
    val mutable hit_test_code: uint32
    val mutable extra_info: unativeint
    val mutable mouse_data: uint32

type EnumThreadWindowCallback = delegate of nativeint * nativeint -> bool

[<UnmanagedFunctionPointer(CallingConvention.Winapi)>]
type HookProcedure = delegate of int * nativeint * nativeint -> nativeint

type WindowsHook =
    { handle: nativeint
      procedure: HookProcedure }

[<DllImport("user32.dll")>]
extern int16 GetAsyncKeyState(int virtual_key)

[<DllImport("user32.dll", SetLastError = true)>]
extern bool GetCursorPos(NativePoint& point)

[<DllImport("user32.dll")>]
extern nativeint WindowFromPoint(NativePoint point)

[<DllImport("user32.dll", SetLastError = true)>]
extern bool SetCursorPos(int x, int y)

[<DllImport("user32.dll", EntryPoint = "ClipCursor", SetLastError = true)>]
extern bool ClipCursorRect(NativeRect& rectangle)

[<DllImport("user32.dll", SetLastError = true)>]
extern bool GetClipCursor(NativeRect& rectangle)

[<DllImport("user32.dll")>]
extern int ShowCursor(bool show)

[<DllImport("user32.dll")>]
extern bool IsChild(nativeint parent, nativeint window)

[<DllImport("user32.dll")>]
extern bool IsWindow(nativeint window)

[<DllImport("user32.dll")>]
extern bool IsWindowEnabled(nativeint window)

[<DllImport("user32.dll")>]
extern nativeint GetForegroundWindow()

[<DllImport("user32.dll")>]
extern nativeint SetFocus(nativeint window)

[<DllImport("user32.dll")>]
extern bool SetForegroundWindow(nativeint window)

[<DllImport("user32.dll")>]
extern nativeint GetAncestor(nativeint window, uint32 flags)

[<DllImport("user32.dll")>]
extern nativeint SendMessage(nativeint window, int message, nativeint wparam, nativeint lparam)

[<DllImport("user32.dll", SetLastError = true)>]
extern bool PostMessage(nativeint window, int message, nativeint wparam, nativeint lparam)

[<DllImport("user32.dll", SetLastError = true)>]
extern uint32 MsgWaitForMultipleObjectsEx(
    uint32 object_count,
    nativeint handles,
    uint32 milliseconds,
    uint32 wake_mask,
    uint32 flags
)

[<DllImport("user32.dll")>]
extern uint32 GetWindowThreadProcessId(nativeint window, uint32& process_id)

[<DllImport("user32.dll", CharSet = CharSet.Unicode)>]
extern int GetClassName(nativeint window, StringBuilder class_name, int maximum_count)

[<DllImport("user32.dll")>]
extern bool IsWindowVisible(nativeint window)

[<DllImport("user32.dll")>]
extern bool EnumThreadWindows(uint32 thread_id, EnumThreadWindowCallback callback, nativeint state)

[<DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)>]
extern nativeint SetWindowsHookEx(int hook_type, HookProcedure procedure, nativeint module_handle, uint32 thread_id)

[<DllImport("user32.dll", SetLastError = true)>]
extern bool UnhookWindowsHookEx(nativeint hook)

[<DllImport("user32.dll")>]
extern nativeint CallNextHookEx(nativeint hook, int code, nativeint wparam, nativeint lparam)

[<DllImport("kernel32.dll")>]
extern uint32 GetCurrentThreadId()

[<DllImport("user32.dll", SetLastError = true)>]
extern bool UpdateWindow(nativeint window)

[<DllImport("user32.dll", SetLastError = true)>]
extern bool RedrawWindow(nativeint window, nativeint update_rectangle, nativeint update_region, uint32 flags)
