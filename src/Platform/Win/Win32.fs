module RhinosCanFly.Platform.Win.Win32

#nowarn "9"

open System
open System.ComponentModel
open System.Drawing
open System.Runtime.InteropServices
open System.Text
open Microsoft.FSharp.NativeInterop
open RhinosCanFly

let win32_error (operation: string) (error_code: int) =
    Win32Exception(error_code)
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
    Win32Native.PostMessage(window, Win32Native.WM_MOUSELEAVE, nativeint 0, nativeint 0)
    |> ignore

let dismiss_native_tooltips (window: nativeint) =
    let mutable process_id = 0u
    let thread_id = Win32Native.GetWindowThreadProcessId(window, &process_id)

    if thread_id <> 0u then
        let callback =
            Win32Native.EnumThreadWindowCallback(fun (candidate: nativeint) (_state: nativeint) ->
                if Win32Native.IsWindowVisible candidate then
                    let class_name = StringBuilder(64)

                    if
                        Win32Native.GetClassName(candidate, class_name, class_name.Capacity) > 0
                        && String.Equals(
                            class_name.ToString(),
                            Win32Native.TOOLTIP_WINDOW_CLASS,
                            StringComparison.OrdinalIgnoreCase
                        )
                    then
                        Win32Native.PostMessage(candidate, Win32Native.TTM_POP, nativeint 0, nativeint 0)
                        |> ignore

                true)

        Win32Native.EnumThreadWindows(thread_id, callback, nativeint 0) |> ignore

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

let request_application_redraw (main_window: nativeint) =
    try
        let foreground_window = Win32Native.GetForegroundWindow()
        request_window_tree_redraw main_window

        if foreground_window <> nativeint 0 && foreground_window <> main_window then
            let mutable main_process = 0u
            let mutable foreground_process = 0u
            Win32Native.GetWindowThreadProcessId(main_window, &main_process) |> ignore

            Win32Native.GetWindowThreadProcessId(foreground_window, &foreground_process)
            |> ignore

            if main_process <> 0u && foreground_process = main_process then
                request_window_tree_redraw foreground_window
    with _ ->
        ()

let wait_for_input_for (timeout_milliseconds: int) =
    let result =
        Win32Native.MsgWaitForMultipleObjectsEx(
            0u,
            nativeint 0,
            uint32 (max 0 timeout_milliseconds),
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
      hook_window: nativeint
      point_window: nativeint
      screen_point: System.Drawing.Point
      modifiers: MouseModifiers }

let modifier_down (general_key: int) (left_key: int) (right_key: int) =
    Win32Native.GetAsyncKeyState general_key < 0s
    || Win32Native.GetAsyncKeyState left_key < 0s
    || Win32Native.GetAsyncKeyState right_key < 0s

let mouse_modifiers () =
    { shift = modifier_down Win32Native.VK_SHIFT Win32Native.VK_LSHIFT Win32Native.VK_RSHIFT
      alt = modifier_down Win32Native.VK_MENU Win32Native.VK_LMENU Win32Native.VK_RMENU
      control = modifier_down Win32Native.VK_CONTROL Win32Native.VK_LCONTROL Win32Native.VK_RCONTROL }

let keyboard_physical_key (virtual_key: int) (event_data: int64) =
    let extended = event_data &&& Win32Native.KEYBOARD_EXTENDED_KEY <> 0L

    let scan_code =
        int (
            (event_data &&& Win32Native.KEYBOARD_SCAN_CODE_MASK)
            >>> Win32Native.KEYBOARD_SCAN_CODE_SHIFT
        )

    match virtual_key with
    | Win32Native.VK_LSHIFT -> Win32Native.VK_LSHIFT
    | Win32Native.VK_RSHIFT -> Win32Native.VK_RSHIFT
    | Win32Native.VK_SHIFT ->
        if scan_code = Win32Native.RIGHT_SHIFT_SCAN_CODE then
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
    | _ -> virtual_key

let install_keyboard_hook (handle_event: KeyboardHookEvent -> bool) =
    let mutable hook = nativeint 0

    let procedure =
        Win32Native.HookProcedure(fun (code: int) (wparam: nativeint) (lparam: nativeint) ->
            let event_data = int64 lparam
            let virtual_key = int wparam

            let event: KeyboardHookEvent =
                { physical_key = keyboard_physical_key virtual_key event_data
                  released = event_data &&& Win32Native.KEYBOARD_KEY_RELEASED <> 0L
                  was_down = event_data &&& Win32Native.KEYBOARD_PREVIOUSLY_DOWN <> 0L }

            if code = Win32Native.HC_ACTION && handle_event event then
                nativeint 1
            else
                Win32Native.CallNextHookEx(hook, code, wparam, lparam))

    hook <-
        Win32Native.SetWindowsHookEx(Win32Native.WH_KEYBOARD, procedure, nativeint 0, Win32Native.GetCurrentThreadId())

    if hook = nativeint 0 then
        Error(last_error "SetWindowsHookEx(WH_KEYBOARD)")
    else
        let keyboard_hook: Win32Native.WindowsHook =
            { handle = hook; procedure = procedure }

        Ok keyboard_hook

let install_mouse_hook (handle_event: MouseHookEvent -> bool) =
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
                let point_window = Win32Native.WindowFromPoint data.point

                let event: MouseHookEvent =
                    { message = message
                      mouse_data = data.mouse_data
                      hook_window = data.window
                      point_window = point_window
                      screen_point = System.Drawing.Point(data.point.x, data.point.y)
                      modifiers = mouse_modifiers () }

                if handle_event event then
                    nativeint 1
                else
                    Win32Native.CallNextHookEx(hook, code, wparam, lparam)
            else
                Win32Native.CallNextHookEx(hook, code, wparam, lparam))

    hook <- Win32Native.SetWindowsHookEx(Win32Native.WH_MOUSE, procedure, nativeint 0, Win32Native.GetCurrentThreadId())

    if hook = nativeint 0 then
        Error(last_error "SetWindowsHookEx(WH_MOUSE)")
    else
        let mouse_hook: Win32Native.WindowsHook = { handle = hook; procedure = procedure }

        Ok mouse_hook

let remove_hook (hook: Win32Native.WindowsHook) =
    let removed = Win32Native.UnhookWindowsHookEx hook.handle
    GC.KeepAlive hook.procedure

    if removed then
        Ok()
    else
        Error(last_error "UnhookWindowsHookEx")
