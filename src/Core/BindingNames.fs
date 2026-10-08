namespace RhinosCanFly

[<Struct>]
type BindingToken =
    | NamedKey of name: string
    | WindowsVirtualKey of code: int

module BindingNames =
    open System
    open System.Collections.Generic
    open System.Globalization
    open Eto.Forms

    let aliases =
        let result = Dictionary<string, int> StringComparer.OrdinalIgnoreCase

        [ "LeftShift", 0xA0
          "LShiftKey", 0xA0
          "RightShift", 0xA1
          "RShiftKey", 0xA1
          "Shift", 0x10
          "ShiftKey", 0x10
          "LeftAlt", 0xA4
          "LMenu", 0xA4
          "RightAlt", 0xA5
          "RMenu", 0xA5
          "Alt", 0x12
          "Menu", 0x12
          "LeftControl", 0xA2
          "LControlKey", 0xA2
          "RightControl", 0xA3
          "RControlKey", 0xA3
          "Control", 0x11
          "ControlKey", 0x11
          "Ctrl", 0x11
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
          "MouseLeft", 0x01
          "LButton", 0x01
          "MouseRight", 0x02
          "RButton", 0x02
          "MouseMiddle", 0x04
          "MButton", 0x04
          "MouseX1", 0x05
          "XButton1", 0x05
          "MouseX2", 0x06
          "XButton2", 0x06
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
        |> List.iter (fun (name: string, virtual_key: int) -> result[name] <- virtual_key)

        for code in int 'A' .. int 'Z' do
            result[string (char code)] <- code

        for digit in 0..9 do
            let top_row = 0x30 + digit
            let number_pad = 0x60 + digit
            result[string digit] <- top_row
            result[$"D{digit}"] <- top_row
            result[$"Number{digit}"] <- top_row
            result[$"NumPad{digit}"] <- number_pad
            result[$"NumberPad{digit}"] <- number_pad
            result[$"Keypad{digit}"] <- number_pad

        for number in 1..24 do
            result[$"F{number}"] <- (0x6F + number)

        result

    let canonical_names =
        let result = Dictionary<int, string>()

        for pair in aliases do
            if not (result.ContainsKey pair.Value) then
                result[pair.Value] <- pair.Key

        result

    let parse_key (text: string) =
        let mutable alias = 0

        if aliases.TryGetValue(text, &alias) then
            Ok(NamedKey canonical_names[alias])
        elif
            text.Equals("Command", StringComparison.OrdinalIgnoreCase)
            || text.Equals("Cmd", StringComparison.OrdinalIgnoreCase)
        then
            Ok(NamedKey "Command")
        elif text.Equals("LeftCommand", StringComparison.OrdinalIgnoreCase) then
            Ok(NamedKey "LeftCommand")
        elif text.Equals("RightCommand", StringComparison.OrdinalIgnoreCase) then
            Ok(NamedKey "RightCommand")
        elif text.Equals("Option", StringComparison.OrdinalIgnoreCase) then
            Ok(NamedKey "Alt")
        elif text.Equals("LeftOption", StringComparison.OrdinalIgnoreCase) then
            Ok(NamedKey "LeftAlt")
        elif text.Equals("RightOption", StringComparison.OrdinalIgnoreCase) then
            Ok(NamedKey "RightAlt")
        elif text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) then
            let mutable value = 0

            if
                Int32.TryParse(text.Substring 2, NumberStyles.HexNumber, CultureInfo.InvariantCulture, &value)
                && value >= 1
                && value <= 0xFF
            then
                Ok(WindowsVirtualKey value)
            else
                Error $"'{text}' is not a hexadecimal virtual-key code between 0x01 and 0xFF"
        else
            Error $"unknown key '{text}'"

    let parse (source: string) =
        if String.IsNullOrWhiteSpace source then
            Error "key name is empty"
        else
            let keys = ResizeArray<BindingToken>()
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
            | None -> Ok(keys.ToArray())

    let key_value (key: Keys) =
        let value = key &&& Keys.KeyMask

        if value = Keys.None && key <> Keys.None then key else value

    let key_name (key: Keys) =
        match key_value key with
        | Keys.LeftShift -> "LeftShift"
        | Keys.RightShift -> "RightShift"
        | Keys.Shift -> "Shift"
        | Keys.LeftAlt -> "LeftAlt"
        | Keys.RightAlt -> "RightAlt"
        | Keys.Alt -> "Alt"
        | Keys.LeftControl -> "LeftControl"
        | Keys.RightControl -> "RightControl"
        | Keys.Control -> "Control"
        | Keys.Escape -> "Escape"
        | Keys.Minus -> "Minus"
        | Keys.Subtract -> "Subtract"
        | Keys.Equal -> "Equals"
        | Keys.Add -> "Add"
        | Keys.Comma -> "Comma"
        | Keys.Period -> "Period"
        | Keys.Decimal -> "Decimal"
        | Keys.Slash -> "Slash"
        | Keys.Divide -> "Divide"
        | Keys.Semicolon -> "Semicolon"
        | Keys.Quote -> "Quote"
        | Keys.LeftBracket -> "LeftBracket"
        | Keys.RightBracket -> "RightBracket"
        | Keys.Backslash -> "Backslash"
        | Keys.Grave -> "Backtick"
        | other -> other.ToString()

    type CaptureNames =
        { key_name: Keys -> string
          modifiers: Keys list
          modifier_group: Keys -> Keys }

    let capture_names =
        { key_name = key_name
          modifiers = [ Keys.Control; Keys.Alt; Keys.Shift ]
          modifier_group =
            fun (key: Keys) ->
                match key with
                | Keys.LeftControl
                | Keys.RightControl -> Keys.Control
                | Keys.LeftAlt
                | Keys.RightAlt -> Keys.Alt
                | Keys.LeftShift
                | Keys.RightShift -> Keys.Shift
                | _ -> key }

    let modifier_names (names: CaptureNames) (modifiers: Keys) =
        names.modifiers
        |> List.choose (fun (key: Keys) ->
            if modifiers &&& key = key then
                Some(names.key_name key)
            else
                None)

    let is_modifier_key (names: CaptureNames) (key: Keys) =
        List.contains (names.modifier_group (key_value key)) names.modifiers

    let chord_name (modifiers: string list) (key: string) = String.concat "+" (modifiers @ [ key ])

    let binding_from_key (names: CaptureNames) (key: Keys) (modifiers: Keys) =
        chord_name (modifier_names names modifiers) (names.key_name key)

    let binding_from_mouse (names: CaptureNames) (button: MouseButtons) (modifiers: Keys) =
        let name =
            match button with
            | MouseButtons.Primary -> Some "MouseLeft"
            | MouseButtons.Alternate -> Some "MouseRight"
            | MouseButtons.Middle -> Some "MouseMiddle"
            | _ -> None

        name |> Option.map (chord_name (modifier_names names modifiers))
