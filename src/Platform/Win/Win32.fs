module RhinosCanFly.Platform.Win.Win32

#nowarn "9"

open System
open System.ComponentModel
open System.Drawing
open System.Runtime.InteropServices
open System.Text
open Microsoft.FSharp.NativeInterop
open RhinosCanFly

let win32_error (operation: string) (errorCode: int) =
    Win32Exception(errorCode)
    |> fun (error: Win32Exception) -> $"{operation} failed: {error.Message}"

let last_error (operation: string) =
    win32_error operation (Marshal.GetLastWin32Error())

let get_cursor_position () =
    let mutable point = Unchecked.defaultof<Win32Native.NativePoint>

    if Win32Native.GetCursorPos(&point) then
        Ok(Point(point.x, point.y))
    else
        Error(last_error "GetCursorPos")

let set_cursor_position (point: Point) =
    if Win32Native.SetCursorPos(point.X, point.Y) then
        Ok()
    else
        Error(last_error "SetCursorPos")

let rectangle_from_native (native: Win32Native.NativeRect) =
    Rectangle.FromLTRB(native.left, native.top, native.right, native.bottom)

let native_rectangle (rectangle: Rectangle) =
    let mutable native = Unchecked.defaultof<Win32Native.NativeRect>
    native.left <- rectangle.Left
    native.top <- rectangle.Top
    native.right <- rectangle.Right
    native.bottom <- rectangle.Bottom
    native

let get_cursor_clip () =
    let mutable native = Unchecked.defaultof<Win32Native.NativeRect>

    if Win32Native.GetClipCursor(&native) then
        Ok(rectangle_from_native native)
    else
        Error(last_error "GetClipCursor")

let clip_cursor (rectangle: Rectangle) =
    let mutable native = native_rectangle rectangle

    if Win32Native.ClipCursorRect(&native) then
        Ok()
    else
        Error(last_error "ClipCursor")

let clear_mouse_hover (window: nativeint) =
    Win32Native.SendMessage(window, Win32Native.WM_MOUSELEAVE, nativeint 0, nativeint 0)
    |> ignore

let dismiss_native_tooltips (window: nativeint) =
    let mutable processId = 0u
    let threadId = Win32Native.GetWindowThreadProcessId(window, &processId)

    if threadId <> 0u then
        let callback =
            Win32Native.EnumThreadWindowCallback(fun (candidate: nativeint) (_state: nativeint) ->
                if Win32Native.IsWindowVisible candidate then
                    let className = StringBuilder(64)

                    if
                        Win32Native.GetClassName(candidate, className, className.Capacity) > 0
                        && String.Equals(
                            className.ToString(),
                            Win32Native.TOOLTIP_WINDOW_CLASS,
                            StringComparison.OrdinalIgnoreCase
                        )
                    then
                        Win32Native.SendMessage(candidate, Win32Native.TTM_POP, nativeint 0, nativeint 0)
                        |> ignore

                true)

        Win32Native.EnumThreadWindows(threadId, callback, nativeint 0) |> ignore

let update_window (window: nativeint) =
    if not (Win32Native.UpdateWindow window) then
        failwith (last_error "UpdateWindow")

let request_window_tree_redraw (window: nativeint) =
    if window <> nativeint 0 && Win32Native.IsWindow window then
        Win32Native.RedrawWindow(
            window,
            nativeint 0,
            nativeint 0,
            Win32Native.RDW_INVALIDATE
            ||| Win32Native.RDW_ALLCHILDREN
            ||| Win32Native.RDW_FRAME
        )
        |> ignore

let request_application_redraw (mainWindow: nativeint) =
    try
        let foregroundWindow = Win32Native.GetForegroundWindow()
        request_window_tree_redraw mainWindow

        if foregroundWindow <> nativeint 0 && foregroundWindow <> mainWindow then
            let mutable mainProcess = 0u
            let mutable foregroundProcess = 0u
            Win32Native.GetWindowThreadProcessId(mainWindow, &mainProcess) |> ignore

            Win32Native.GetWindowThreadProcessId(foregroundWindow, &foregroundProcess)
            |> ignore

            if mainProcess <> 0u && foregroundProcess = mainProcess then
                request_window_tree_redraw foregroundWindow
    with _ ->
        ()

let wait_for_input_for (timeoutMilliseconds: int) =
    let result =
        Win32Native.MsgWaitForMultipleObjectsEx(
            0u,
            nativeint 0,
            uint32 (max 0 timeoutMilliseconds),
            Win32Native.QS_ALLINPUT,
            Win32Native.MWMO_INPUTAVAILABLE
        )

    if result = Win32Native.WAIT_FAILED then
        failwith (last_error "MsgWaitForMultipleObjectsEx")

[<Struct>]
type KeyboardHookEvent =
    { physical_key: int
      released: bool
      was_down: bool }

[<Struct>]
type MouseHookEvent =
    { message: int
      mouse_data: uint32
      point_window: nativeint
      screen_point: System.Drawing.Point
      modifiers: MouseModifiers }

let modifier_down (generalKey: int) (leftKey: int) (rightKey: int) =
    Win32Native.GetAsyncKeyState generalKey < 0s
    || Win32Native.GetAsyncKeyState leftKey < 0s
    || Win32Native.GetAsyncKeyState rightKey < 0s

let mouse_modifiers () =
    { shift = modifier_down Win32Native.VK_SHIFT Win32Native.VK_LSHIFT Win32Native.VK_RSHIFT
      alt = modifier_down Win32Native.VK_MENU Win32Native.VK_LMENU Win32Native.VK_RMENU
      control = modifier_down Win32Native.VK_CONTROL Win32Native.VK_LCONTROL Win32Native.VK_RCONTROL }

let keyboard_physical_key (virtualKey: int) (eventData: int64) =
    let extended = eventData &&& Win32Native.KEYBOARD_EXTENDED_KEY <> 0L

    let scanCode =
        int (
            (eventData &&& Win32Native.KEYBOARD_SCAN_CODE_MASK)
            >>> Win32Native.KEYBOARD_SCAN_CODE_SHIFT
        )

    match virtualKey with
    | Win32Native.VK_LSHIFT -> Win32Native.VK_LSHIFT
    | Win32Native.VK_RSHIFT -> Win32Native.VK_RSHIFT
    | Win32Native.VK_SHIFT ->
        if scanCode = Win32Native.RIGHT_SHIFT_SCAN_CODE then
            Win32Native.VK_RSHIFT
        else
            Win32Native.VK_LSHIFT
    | Win32Native.VK_LCONTROL -> Win32Native.VK_LCONTROL
    | Win32Native.VK_RCONTROL -> Win32Native.VK_RCONTROL
    | Win32Native.VK_CONTROL ->
        if extended then
            Win32Native.VK_RCONTROL
        else
            Win32Native.VK_LCONTROL
    | Win32Native.VK_LMENU -> Win32Native.VK_LMENU
    | Win32Native.VK_RMENU -> Win32Native.VK_RMENU
    | Win32Native.VK_MENU ->
        if extended then
            Win32Native.VK_RMENU
        else
            Win32Native.VK_LMENU
    | _ -> virtualKey

let install_keyboard_hook (handleEvent: KeyboardHookEvent -> bool) =
    let mutable hook = nativeint 0

    let procedure =
        Win32Native.HookProcedure(fun (code: int) (wparam: nativeint) (lparam: nativeint) ->
            let eventData = int64 lparam
            let virtualKey = int wparam

            let event: KeyboardHookEvent =
                { physical_key = keyboard_physical_key virtualKey eventData
                  released = eventData &&& Win32Native.KEYBOARD_KEY_RELEASED <> 0L
                  was_down = eventData &&& Win32Native.KEYBOARD_PREVIOUSLY_DOWN <> 0L }

            if code = Win32Native.HC_ACTION && handleEvent event then
                nativeint 1
            else
                Win32Native.CallNextHookEx(hook, code, wparam, lparam))

    hook <-
        Win32Native.SetWindowsHookEx(Win32Native.WH_KEYBOARD, procedure, nativeint 0, Win32Native.GetCurrentThreadId())

    if hook = nativeint 0 then
        Error(last_error "SetWindowsHookEx(WH_KEYBOARD)")
    else
        let keyboardHook: Win32Native.WindowsHook = { handle = hook; procedure = procedure }

        Ok keyboardHook

let install_mouse_hook (handleEvent: MouseHookEvent -> bool) =
    let mutable hook = nativeint 0

    let procedure =
        Win32Native.HookProcedure(fun (code: int) (wparam: nativeint) (lparam: nativeint) ->
            let message = int wparam

            if
                code = Win32Native.HC_ACTION
                && (message = Win32Native.WM_RBUTTONDOWN
                    || message = Win32Native.WM_RBUTTONUP
                    || message = Win32Native.WM_RBUTTONDBLCLK
                    || message = Win32Native.WM_MBUTTONDOWN
                    || message = Win32Native.WM_MBUTTONUP
                    || message = Win32Native.WM_MBUTTONDBLCLK
                    || message = Win32Native.WM_XBUTTONDOWN
                    || message = Win32Native.WM_XBUTTONUP
                    || message = Win32Native.WM_XBUTTONDBLCLK)
            then
                let data = NativePtr.read (NativePtr.ofNativeInt<Win32Native.MouseHookData> lparam)
                let pointWindow = Win32Native.WindowFromPoint data.point

                let event: MouseHookEvent =
                    { message = message
                      mouse_data = data.mouse_data
                      point_window = pointWindow
                      screen_point = System.Drawing.Point(data.point.x, data.point.y)
                      modifiers = mouse_modifiers () }

                if handleEvent event then
                    nativeint 1
                else
                    Win32Native.CallNextHookEx(hook, code, wparam, lparam)
            else
                Win32Native.CallNextHookEx(hook, code, wparam, lparam))

    hook <- Win32Native.SetWindowsHookEx(Win32Native.WH_MOUSE, procedure, nativeint 0, Win32Native.GetCurrentThreadId())

    if hook = nativeint 0 then
        Error(last_error "SetWindowsHookEx(WH_MOUSE)")
    else
        let mouseHook: Win32Native.WindowsHook = { handle = hook; procedure = procedure }

        Ok mouseHook

let remove_hook (hook: Win32Native.WindowsHook) =
    let removed = Win32Native.UnhookWindowsHookEx hook.handle
    GC.KeepAlive hook.procedure

    if removed then
        Ok()
    else
        Error(last_error "UnhookWindowsHookEx")
