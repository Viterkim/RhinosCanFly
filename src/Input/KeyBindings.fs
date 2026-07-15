module RhinosCanFly.KeyBindings

open System
open System.Collections.Generic
open System.Globalization
open System.Windows.Forms

let aliases =
    let d = Dictionary<string, Keys> StringComparer.OrdinalIgnoreCase

    [ "LeftShift", Keys.LShiftKey
      "RightShift", Keys.RShiftKey
      "Shift", Keys.ShiftKey
      "LeftAlt", Keys.LMenu
      "RightAlt", Keys.RMenu
      "Alt", Keys.Menu
      "LeftControl", Keys.LControlKey
      "RightControl", Keys.RControlKey
      "Control", Keys.ControlKey
      "Ctrl", Keys.ControlKey
      "ArrowUp", Keys.Up
      "ArrowDown", Keys.Down
      "ArrowLeft", Keys.Left
      "ArrowRight", Keys.Right
      "Escape", Keys.Escape
      "Esc", Keys.Escape
      "Space", Keys.Space
      "Enter", Keys.Enter
      "Tab", Keys.Tab
      "Backspace", Keys.Back
      "PageUp", Keys.PageUp
      "PageDown", Keys.PageDown
      "Home", Keys.Home
      "End", Keys.End
      "Insert", Keys.Insert
      "Delete", Keys.Delete
      "MouseLeft", Keys.LButton
      "MouseRight", Keys.RButton
      "MouseMiddle", Keys.MButton
      "MouseX1", Keys.XButton1
      "MouseX2", Keys.XButton2
      "Minus", Keys.OemMinus
      "Equals", Keys.Oemplus
      "Plus", Keys.Oemplus
      "Comma", Keys.Oemcomma
      "Period", Keys.OemPeriod
      "Slash", Keys.OemQuestion
      "Semicolon", Keys.OemSemicolon
      "Quote", Keys.OemQuotes
      "LeftBracket", Keys.OemOpenBrackets
      "RightBracket", Keys.OemCloseBrackets
      "Backslash", Keys.OemPipe
      "Backtick", Keys.Oemtilde ]
    |> List.iter (fun (k: string, v: Keys) -> d[k] <- v)

    d

let parse (source: string) =
    if String.IsNullOrWhiteSpace source then
        Error "key name is empty"
    else
        let text = source.Trim()
        let mutable alias = Unchecked.defaultof<Keys>

        if aliases.TryGetValue(text, &alias) then
            Ok
                { source = source
                  virtual_key = int alias }
        elif text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) then
            let mutable value = 0

            if Int32.TryParse(text.Substring 2, NumberStyles.HexNumber, CultureInfo.InvariantCulture, &value) then
                Ok { source = source; virtual_key = value }
            else
                Error $"'{source}' is not a valid hexadecimal virtual-key code"
        else
            let mutable key = Unchecked.defaultof<Keys>

            if Enum.TryParse<Keys>(text, true, &key) then
                Ok
                    { source = source
                      virtual_key = int key }
            else
                Error $"unknown key '{source}'"

let is_down (binding: KeyBinding) =
    Win32.GetAsyncKeyState binding.virtual_key < 0s
