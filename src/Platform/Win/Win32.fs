module RhinosCanFly.Platform.Win.Win32

open System
open System.ComponentModel
open System.Drawing
open System.Runtime.InteropServices
open System.Text

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

let clip_cursor (rectangle: Rectangle) =
    let mutable native = Unchecked.defaultof<Win32Native.NativeRect>
    native.left <- rectangle.Left
    native.top <- rectangle.Top
    native.right <- rectangle.Right
    native.bottom <- rectangle.Bottom

    if Win32Native.ClipCursorRect(&native) then
        Ok()
    else
        Error(last_error "ClipCursor")

let clear_cursor_clip () =
    if Win32Native.ClipCursorClear(nativeint 0) then
        Ok()
    else
        Error(last_error "ClipCursor(null)")

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
    if Win32Native.UpdateWindow window then
        Ok()
    else
        Error(last_error "UpdateWindow")

let redraw_window (window: nativeint) =
    if not (Win32Native.InvalidateRect(window, nativeint 0, false)) then
        Error(last_error "InvalidateRect")
    else
        update_window window

let wait_for_input (timeoutMilliseconds: uint32) =
    let result =
        Win32Native.MsgWaitForMultipleObjectsEx(
            0u,
            nativeint 0,
            timeoutMilliseconds,
            Win32Native.QS_ALLINPUT,
            Win32Native.MWMO_INPUTAVAILABLE
        )

    if result = Win32Native.WAIT_FAILED then
        Error(last_error "MsgWaitForMultipleObjectsEx")
    else
        Ok()

let install_keyboard_hook (handleKeyDown: int -> bool) =
    let mutable hook = nativeint 0

    let procedure =
        Win32Native.HookProcedure(fun (code: int) (wparam: nativeint) (lparam: nativeint) ->
            let keyReleased = int64 lparam &&& (1L <<< 31) <> 0L

            if code = Win32Native.HC_ACTION && not keyReleased && handleKeyDown (int wparam) then
                nativeint 1
            else
                Win32Native.CallNextHookEx(hook, code, wparam, lparam))

    hook <-
        Win32Native.SetWindowsHookEx(Win32Native.WH_KEYBOARD, procedure, nativeint 0, Win32Native.GetCurrentThreadId())

    if hook = nativeint 0 then
        Error(last_error "SetWindowsHookEx(WH_KEYBOARD)")
    else
        let keyboardHook: Win32Native.KeyboardHook =
            { handle = hook; procedure = procedure }

        Ok keyboardHook

let remove_keyboard_hook (hook: Win32Native.KeyboardHook) =
    let removed = Win32Native.UnhookWindowsHookEx hook.handle
    GC.KeepAlive hook.procedure

    if removed then
        Ok()
    else
        Error(last_error "UnhookWindowsHookEx")

let mouse_input (flags: uint32) =
    let mutable mouse = Unchecked.defaultof<Win32Native.MouseInput>
    mouse.flags <- flags

    let mutable input = Unchecked.defaultof<Win32Native.NativeInput>
    input.input_type <- Win32Native.INPUT_MOUSE

    let mutable data = Unchecked.defaultof<Win32Native.InputData>
    data.mouse <- mouse
    input.data <- data
    input

let keyboard_input (virtualKey: int) (flags: uint32) =
    let mutable keyboard = Unchecked.defaultof<Win32Native.KeyboardInput>
    keyboard.virtual_key <- uint16 virtualKey
    keyboard.flags <- flags

    let mutable input = Unchecked.defaultof<Win32Native.NativeInput>
    input.input_type <- Win32Native.INPUT_KEYBOARD

    let mutable data = Unchecked.defaultof<Win32Native.InputData>
    data.keyboard <- keyboard
    input.data <- data
    input

let try_send_inputs (operation: string) (inputs: Win32Native.NativeInput array) =
    let sent =
        Win32Native.SendInput(uint32 inputs.Length, inputs, Marshal.SizeOf<Win32Native.NativeInput>())

    if sent = uint32 inputs.Length then
        Ok()
    else
        let error = last_error operation
        Error(struct (sent, $"{error} ({sent} of {inputs.Length} events inserted)"))

let send_inputs (operation: string) (inputs: Win32Native.NativeInput array) =
    match try_send_inputs operation inputs with
    | Ok() -> Ok()
    | Error(struct (_, error)) -> Error error

let send_middle_mouse (down: bool) =
    let flags =
        if down then
            Win32Native.MOUSEEVENTF_MIDDLEDOWN
        else
            Win32Native.MOUSEEVENTF_MIDDLEUP

    send_inputs "SendInput(middle mouse)" [| mouse_input flags |]

let send_shift_key (down: bool) =
    let flags = if down then 0u else Win32Native.KEYEVENTF_KEYUP
    send_inputs "SendInput(shift)" [| keyboard_input Win32Native.VK_SHIFT flags |]

let start_shift_middle_mouse () =
    match
        try_send_inputs
            "SendInput(shift + middle mouse)"
            [| keyboard_input Win32Native.VK_SHIFT 0u
               mouse_input Win32Native.MOUSEEVENTF_MIDDLEDOWN |]
    with
    | Ok() -> Ok()
    | Error(struct (1u, error)) ->
        match send_shift_key false with
        | Ok() -> Error error
        | Error cleanupError -> Error $"{error}; {cleanupError}"
    | Error(struct (_, error)) -> Error error

let stop_shift_middle_mouse () =
    match
        try_send_inputs
            "SendInput(middle mouse + shift)"
            [| mouse_input Win32Native.MOUSEEVENTF_MIDDLEUP
               keyboard_input Win32Native.VK_SHIFT Win32Native.KEYEVENTF_KEYUP |]
    with
    | Ok() -> Ok()
    | Error(struct (1u, error)) ->
        match send_shift_key false with
        | Ok() -> Ok()
        | Error cleanupError -> Error $"{error}; {cleanupError}"
    | Error(struct (_, error)) -> Error error
