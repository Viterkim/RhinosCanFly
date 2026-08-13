module RhinosCanFly.Platform.Win.Win32

#nowarn "9"

open System
open System.ComponentModel
open System.Drawing
open System.Runtime.InteropServices
open System.Text
open Microsoft.FSharp.NativeInterop
open RhinosCanFly

let nativeInputSize = Marshal.SizeOf<Win32Native.NativeInput>()
let cursorClipRecoveries = ResizeArray<CursorClipLease>()

let retain_cursor_clip_recovery (lease: CursorClipLease) =
    let exists =
        cursorClipRecoveries
        |> Seq.exists (fun (candidate: CursorClipLease) -> Object.ReferenceEquals(candidate, lease))

    if not exists then
        cursorClipRecoveries.Add lease

let forget_cursor_clip_recovery (lease: CursorClipLease) =
    let mutable index = cursorClipRecoveries.Count - 1

    while index >= 0 do
        if Object.ReferenceEquals(cursorClipRecoveries[index], lease) then
            cursorClipRecoveries.RemoveAt index

        index <- index - 1

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

let rec acquire_cursor_clip (rectangle: Rectangle) =
    match get_cursor_clip () with
    | Error error -> Error error
    | Ok previous ->
        match clip_cursor rectangle with
        | Error error -> Error error
        | Ok() ->
            let lease =
                { previous = previous
                  installed = rectangle
                  relinquished = false }

            let verification = get_cursor_clip ()

            match verification with
            | Ok current when current = rectangle -> Ok lease
            | _ ->
                let verificationError =
                    match verification with
                    | Ok _ -> "The cursor clip changed before it could be verified."
                    | Error error -> $"The cursor clip could not be verified: {error}"

                match release_cursor_clip lease with
                | Ok() -> Error verificationError
                | Error releaseError -> Error $"{verificationError}; cleanup failed: {releaseError}"

and release_cursor_clip (lease: CursorClipLease) =
    if lease.relinquished then
        forget_cursor_clip_recovery lease
        Ok()
    else
        match get_cursor_clip () with
        | Error error -> Error error
        | Ok current when current <> lease.installed ->
            lease.relinquished <- true
            forget_cursor_clip_recovery lease
            Ok()
        | Ok _ ->
            match clip_cursor lease.previous with
            | Ok() ->
                lease.relinquished <- true
                forget_cursor_clip_recovery lease
                Ok()
            | Error error ->
                retain_cursor_clip_recovery lease
                Error error

let retry_cursor_clip_cleanup () =
    let pending = cursorClipRecoveries.ToArray()
    let errors = ResizeArray<string>()

    for lease in pending do
        match release_cursor_clip lease with
        | Ok() -> ()
        | Error error -> errors.Add error

    struct (cursorClipRecoveries.Count, List.ofSeq errors)

let cursor_clip_recovery_count () = cursorClipRecoveries.Count

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

let redraw_window (window: nativeint) =
    if not (Win32Native.InvalidateRect(window, nativeint 0, false)) then
        failwith (last_error "InvalidateRect")

    update_window window

let wait_for_input () =
    let result =
        Win32Native.MsgWaitForMultipleObjectsEx(
            0u,
            nativeint 0,
            Win32Native.INFINITE,
            Win32Native.QS_ALLINPUT,
            Win32Native.MWMO_INPUTAVAILABLE
        )

    if result = Win32Native.WAIT_FAILED then
        failwith (last_error "MsgWaitForMultipleObjectsEx")

[<Struct>]
type KeyboardHookEvent =
    { physical_key: int
      released: bool
      was_down: bool
      extra_info: unativeint }

[<Struct>]
type MouseHookEvent =
    { message: int
      mouse_data: uint32
      hook_window: nativeint
      point_window: nativeint
      extra_info: unativeint }

let injected_input_marker = unativeint 0x52434657u

let injected_input (extraInfo: unativeint) = extraInfo = injected_input_marker

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
                  was_down = eventData &&& Win32Native.KEYBOARD_PREVIOUSLY_DOWN <> 0L
                  extra_info = unativeint (Win32Native.GetMessageExtraInfo()) }

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
                && (message = Win32Native.WM_XBUTTONDOWN
                    || message = Win32Native.WM_XBUTTONUP
                    || message = Win32Native.WM_XBUTTONDBLCLK
                    || message = Win32Native.WM_MBUTTONDOWN
                    || message = Win32Native.WM_MBUTTONUP)
            then
                let data = NativePtr.read (NativePtr.ofNativeInt<Win32Native.MouseHookData> lparam)
                let pointWindow = Win32Native.WindowFromPoint data.point

                let event: MouseHookEvent =
                    { message = message
                      mouse_data = data.mouse_data
                      hook_window = data.window
                      point_window = pointWindow
                      extra_info = data.extra_info }

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

let mouse_input (flags: uint32) =
    let mutable mouse = Unchecked.defaultof<Win32Native.MouseInput>
    mouse.flags <- flags
    mouse.extra_info <- injected_input_marker

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
    keyboard.extra_info <- injected_input_marker

    let mutable input = Unchecked.defaultof<Win32Native.NativeInput>
    input.input_type <- Win32Native.INPUT_KEYBOARD

    let mutable data = Unchecked.defaultof<Win32Native.InputData>
    data.keyboard <- keyboard
    input.data <- data
    input

let try_send_inputs (operation: string) (inputs: Win32Native.NativeInput array) =
    let sent = Win32Native.SendInput(uint32 inputs.Length, inputs, nativeInputSize)

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
