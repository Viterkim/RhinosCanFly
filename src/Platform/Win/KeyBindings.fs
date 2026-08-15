module RhinosCanFly.Platform.Win.KeyBindings

open System
open System.Collections.Generic
open System.Globalization
open RhinosCanFly

let aliases =
    let d = Dictionary<string, VirtualKey> StringComparer.OrdinalIgnoreCase

    [ "LeftShift", Win32Native.VK_LSHIFT
      "LShiftKey", Win32Native.VK_LSHIFT
      "RightShift", Win32Native.VK_RSHIFT
      "RShiftKey", Win32Native.VK_RSHIFT
      "Shift", Win32Native.VK_SHIFT
      "ShiftKey", Win32Native.VK_SHIFT
      "LeftAlt", Win32Native.VK_LMENU
      "LMenu", Win32Native.VK_LMENU
      "RightAlt", Win32Native.VK_RMENU
      "RMenu", Win32Native.VK_RMENU
      "Alt", Win32Native.VK_MENU
      "Menu", Win32Native.VK_MENU
      "LeftControl", Win32Native.VK_LCONTROL
      "LControlKey", Win32Native.VK_LCONTROL
      "RightControl", Win32Native.VK_RCONTROL
      "RControlKey", Win32Native.VK_RCONTROL
      "Control", Win32Native.VK_CONTROL
      "ControlKey", Win32Native.VK_CONTROL
      "Ctrl", Win32Native.VK_CONTROL
      "ArrowUp", 0x26
      "Up", 0x26
      "ArrowDown", 0x28
      "Down", 0x28
      "ArrowLeft", 0x25
      "Left", 0x25
      "ArrowRight", 0x27
      "Right", 0x27
      "Escape", 0x1B
      "Esc", 0x1B
      "Space", 0x20
      "Enter", 0x0D
      "Return", 0x0D
      "Tab", 0x09
      "Backspace", 0x08
      "Back", 0x08
      "PageUp", 0x21
      "Prior", 0x21
      "PageDown", 0x22
      "Next", 0x22
      "Home", 0x24
      "End", 0x23
      "Insert", 0x2D
      "Delete", 0x2E
      "CapsLock", 0x14
      "Capital", 0x14
      "Pause", 0x13
      "Clear", 0x0C
      "Help", 0x2F
      "PrintScreen", 0x2C
      "Snapshot", 0x2C
      "NumLock", 0x90
      "NumberLock", 0x90
      "ScrollLock", 0x91
      "Scroll", 0x91
      "LeftWindows", 0x5B
      "LWin", 0x5B
      "LeftApplication", 0x5B
      "RightWindows", 0x5C
      "RWin", 0x5C
      "RightApplication", 0x5C
      "Applications", 0x5D
      "Apps", 0x5D
      "ContextMenu", 0x5D
      "MouseLeft", Win32Native.VK_LBUTTON
      "LButton", Win32Native.VK_LBUTTON
      "MouseRight", Win32Native.VK_RBUTTON
      "RButton", Win32Native.VK_RBUTTON
      "MouseMiddle", Win32Native.VK_MBUTTON
      "MButton", Win32Native.VK_MBUTTON
      "MouseX1", Win32Native.VK_XBUTTON1
      "XButton1", Win32Native.VK_XBUTTON1
      "MouseX2", Win32Native.VK_XBUTTON2
      "XButton2", Win32Native.VK_XBUTTON2
      "Minus", 0xBD
      "OemMinus", 0xBD
      "Equals", 0xBB
      "Plus", 0xBB
      "Oemplus", 0xBB
      "Comma", 0xBC
      "Oemcomma", 0xBC
      "Period", 0xBE
      "OemPeriod", 0xBE
      "Slash", 0xBF
      "ForwardSlash", 0xBF
      "OemQuestion", 0xBF
      "Semicolon", 0xBA
      "OemSemicolon", 0xBA
      "Quote", 0xDE
      "OemQuotes", 0xDE
      "LeftBracket", 0xDB
      "OemOpenBrackets", 0xDB
      "RightBracket", 0xDD
      "OemCloseBrackets", 0xDD
      "Backslash", 0xDC
      "OemPipe", 0xDC
      "Backtick", 0xC0
      "Oemtilde", 0xC0
      "Oem102", 0xE2
      "Multiply", 0x6A
      "Add", 0x6B
      "KeypadEqual", 0x92
      "Separator", 0x6C
      "Subtract", 0x6D
      "Decimal", 0x6E
      "Divide", 0x6F ]
    |> List.iter (fun (name: string, virtualKey: int) -> d[name] <- VirtualKey virtualKey)

    for code in int 'A' .. int 'Z' do
        d[string (char code)] <- VirtualKey code

    for digit in 0..9 do
        let topRow = 0x30 + digit
        let numberPad = 0x60 + digit
        d[string digit] <- VirtualKey topRow
        d[$"D{digit}"] <- VirtualKey topRow
        d[$"Number{digit}"] <- VirtualKey topRow
        d[$"NumPad{digit}"] <- VirtualKey numberPad
        d[$"NumberPad{digit}"] <- VirtualKey numberPad
        d[$"Keypad{digit}"] <- VirtualKey numberPad

    for number in 1..24 do
        d[$"F{number}"] <- VirtualKey(0x6F + number)

    d

let parse_key (text: string) =
    let mutable alias = VirtualKey 0

    if aliases.TryGetValue(text, &alias) then
        Ok alias
    elif text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) then
        let mutable value = 0

        if
            Int32.TryParse(text.Substring 2, NumberStyles.HexNumber, CultureInfo.InvariantCulture, &value)
            && value >= 1
            && value <= 0xFF
        then
            Ok(VirtualKey value)
        else
            Error $"'{text}' is not a hexadecimal virtual-key code between 0x01 and 0xFF"
    else
        Error $"unknown key '{text}'"

let parse (source: string) =
    if String.IsNullOrWhiteSpace source then
        Error "key name is empty"
    else
        let keys = ResizeArray<VirtualKey>()
        let mutable error = None

        for part in source.Split '+' do
            let text = part.Trim()

            if Option.isNone error then
                if String.IsNullOrWhiteSpace text then
                    error <- Some $"invalid key combination '{source}'"
                else
                    match parse_key text with
                    | Ok key ->
                        if not (keys.Contains key) then
                            keys.Add key
                    | Error message -> error <- Some message

        match error with
        | Some message -> Error message
        | None -> Ok { virtual_keys = keys.ToArray() }

let is_down (binding: KeyBinding) =
    let keys = binding.virtual_keys
    let mutable index = 0
    let mutable down = keys.Length > 0

    while down && index < keys.Length do
        let (VirtualKey key) = keys[index]
        down <- Win32Native.GetAsyncKeyState key < 0s
        index <- index + 1

    down
