module RhinosCanFly.PlatformBindings

open System
open System.Collections.Generic
open Eto.Forms
open RhinosCanFly.Platform.Mac

let empty: KeyBinding =
    { keys = [||]
      native_keys = [||]
      unsupported = None }

// 128..132 are mouse buttons; 133..136 are either-side modifiers.
let key_codes =
    let codes = Dictionary<string, int> StringComparer.OrdinalIgnoreCase

    for name, code in
        [ "A", 0
          "S", 1
          "D", 2
          "F", 3
          "H", 4
          "G", 5
          "Z", 6
          "X", 7
          "C", 8
          "V", 9
          "B", 11
          "Q", 12
          "W", 13
          "E", 14
          "R", 15
          "Y", 16
          "T", 17
          "D1", 18
          "D2", 19
          "D3", 20
          "D4", 21
          "D6", 22
          "D5", 23
          "Equals", 24
          "D9", 25
          "D7", 26
          "Minus", 27
          "D8", 28
          "D0", 29
          "RightBracket", 30
          "O", 31
          "U", 32
          "LeftBracket", 33
          "I", 34
          "P", 35
          "Enter", 36
          "L", 37
          "J", 38
          "Quote", 39
          "K", 40
          "Semicolon", 41
          "Backslash", 42
          "Comma", 43
          "Slash", 44
          "N", 45
          "M", 46
          "Period", 47
          "Tab", 48
          "Space", 49
          "Backtick", 50
          "Backspace", 51
          "Escape", 53
          "RightCommand", 54
          "LeftCommand", 55
          "LeftShift", 56
          "CapsLock", 57
          "LeftAlt", 58
          "LeftControl", 59
          "RightShift", 60
          "RightAlt", 61
          "RightControl", 62
          "F17", 64
          "Decimal", 65
          "Multiply", 67
          "Add", 69
          "Clear", 71
          "Divide", 75
          "Subtract", 78
          "F18", 79
          "F19", 80
          "KeypadEqual", 81
          "NumPad0", 82
          "NumPad1", 83
          "NumPad2", 84
          "NumPad3", 85
          "NumPad4", 86
          "NumPad5", 87
          "NumPad6", 88
          "NumPad7", 89
          "F20", 90
          "NumPad8", 91
          "NumPad9", 92
          "F5", 96
          "F6", 97
          "F7", 98
          "F3", 99
          "F8", 100
          "F9", 101
          "F11", 103
          "F13", 105
          "F16", 106
          "F14", 107
          "F10", 109
          "F12", 111
          "F15", 113
          "Help", 114
          "Home", 115
          "PageUp", 116
          "Delete", 117
          "F4", 118
          "End", 119
          "F2", 120
          "PageDown", 121
          "F1", 122
          "ArrowLeft", 123
          "ArrowRight", 124
          "ArrowDown", 125
          "ArrowUp", 126
          "MouseLeft", 128
          "MouseRight", 129
          "MouseMiddle", 130
          "MouseX1", 131
          "MouseX2", 132
          "Shift", 133
          "Control", 134
          "Alt", 135
          "Command", 136
          "LeftWindows", 55
          "RightWindows", 54 ] do
        codes.Add(name, code)

    for digit = 0 to 9 do
        codes[string digit] <- codes[$"D{digit}"]

    codes

let physical_names =
    let names = Dictionary<int, string>()

    for KeyValue(name, code) in key_codes do
        if code < 128 && not (names.ContainsKey code) then
            names.Add(code, name)

    names.Add(52, "Enter")
    names.Add(76, "Enter")
    names

let capture_key_value (key: Keys) =
    match (MacNative.load ()).capture_key.Invoke() with
    | 54u -> Keys.RightApplication
    | 55u -> Keys.LeftApplication
    | 56u -> Keys.LeftShift
    | 58u -> Keys.LeftAlt
    | 59u -> Keys.LeftControl
    | 60u -> Keys.RightShift
    | 61u -> Keys.RightAlt
    | 62u -> Keys.RightControl
    | code when code < 128u && physical_names.ContainsKey(int code) -> key
    | code -> invalidOp $"The current Mac key event has no supported physical key mapping ({code})."

let capture_names: BindingNames.CaptureNames =
    { key_name =
        fun (key: Keys) ->
            match BindingNames.key_value key with
            | Keys.Application -> "Command"
            | Keys.LeftApplication -> "LeftCommand"
            | Keys.RightApplication -> "RightCommand"
            | other when BindingNames.is_modifier_key BindingNames.capture_names other -> BindingNames.key_name other
            | _ ->
                let code = (MacNative.load ()).capture_key.Invoke()

                match physical_names.TryGetValue(int code) with
                | true, name -> name
                | _ -> invalidOp $"The current Mac key event has no supported physical key mapping ({code})."
      modifiers = BindingNames.capture_names.modifiers @ [ Keys.Application ]
      modifier_group =
        fun (key: Keys) ->
            match key with
            | Keys.LeftApplication
            | Keys.RightApplication -> Keys.Application
            | _ -> BindingNames.capture_names.modifier_group key }

let capture_mouse (_buttons: MouseButtons) (modifiers: Keys) =
    let button = (MacNative.load ()).capture_button.Invoke()

    let name =
        match button with
        | 0u -> "MouseLeft"
        | 1u -> "MouseRight"
        | 2u -> "MouseMiddle"
        | 3u -> "MouseX1"
        | 4u -> "MouseX2"
        | _ -> invalidOp $"The current Mac mouse event has no supported physical button ({button})."

    struct (Some(BindingNames.chord_name (BindingNames.modifier_names capture_names modifiers) name), button = 0u)

let parse (source: string) =
    BindingNames.parse source
    |> Result.map (fun (keys: BindingToken array) ->
        let unsupported =
            keys
            |> Array.tryFind (function
                | WindowsVirtualKey _ -> true
                | NamedKey name -> not (key_codes.ContainsKey name))

        { keys = keys
          unsupported = unsupported
          native_keys =
            if Option.isSome unsupported then
                [||]
            else
                keys
                |> Array.map (function
                    | NamedKey name -> key_codes[name]
                    | WindowsVirtualKey _ -> invalidOp "Unsupported binding reached Mac key mapping.")
                |> Array.distinct })

let execution_error (binding: KeyBinding) =
    binding.unsupported
    |> Option.map (function
        | WindowsVirtualKey code -> $"0x{code:X2} is a Windows binding. Choose a named key on Mac."
        | NamedKey name -> $"'{name}' has no Mac key mapping. Choose another binding on Mac.")

let physical_key_down (code: int) =
    match code with
    | 128
    | 129
    | 130
    | 131
    | 132 -> MacNative.mouse_down (code - 128)
    | 133 -> MacNative.key_down 56 || MacNative.key_down 60
    | 134 -> MacNative.key_down 59 || MacNative.key_down 62
    | 135 -> MacNative.key_down 58 || MacNative.key_down 61
    | 136 -> MacNative.key_down 55 || MacNative.key_down 54
    | code when code >= 0 && code < 128 -> MacNative.key_down code
    | _ -> false

let try_side_mouse_binding () =
    let name =
        if MacNative.mouse_down 3 then Some "MouseX1"
        elif MacNative.mouse_down 4 then Some "MouseX2"
        else None

    let modifiers =
        [ if physical_key_down 134 then
              "Control"
          if physical_key_down 135 then
              "Alt"
          if physical_key_down 133 then
              "Shift"
          if physical_key_down 136 then
              "Command" ]

    name |> Option.map (BindingNames.chord_name modifiers)
