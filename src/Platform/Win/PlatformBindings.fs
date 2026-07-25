module RhinosCanFly.PlatformBindings

open System
open Eto.Forms
open RhinosCanFly.Platform.Win

let parse (source: string) = KeyBindings.parse source

let is_down (binding: KeyBinding) = KeyBindings.is_down binding

let private key_value (key: Keys) =
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
    | Keys.Minus
    | Keys.Subtract -> "Minus"
    | Keys.Equal
    | Keys.Add -> "Equals"
    | Keys.Comma -> "Comma"
    | Keys.Period
    | Keys.Decimal -> "Period"
    | Keys.Slash
    | Keys.Divide -> "Slash"
    | Keys.Semicolon -> "Semicolon"
    | Keys.Quote -> "Quote"
    | Keys.LeftBracket -> "LeftBracket"
    | Keys.RightBracket -> "RightBracket"
    | Keys.Backslash -> "Backslash"
    | Keys.Grave -> "Backtick"
    | other -> other.ToString()

let modifier_names (modifiers: Keys) =
    [ if modifiers &&& Keys.Control = Keys.Control then
          "Control"
      if modifiers &&& Keys.Alt = Keys.Alt then
          "Alt"
      if modifiers &&& Keys.Shift = Keys.Shift then
          "Shift" ]

let is_modifier_key (key: Keys) =
    match key_value key with
    | Keys.Shift
    | Keys.LeftShift
    | Keys.RightShift
    | Keys.Control
    | Keys.LeftControl
    | Keys.RightControl
    | Keys.Alt
    | Keys.LeftAlt
    | Keys.RightAlt -> true
    | _ -> false

let private chord_name (modifiers: string list) (key: string) = String.concat "+" (modifiers @ [ key ])

let binding_from_key (key: Keys) (modifiers: Keys) =
    chord_name (modifier_names modifiers) (key_name key)

let binding_from_mouse (button: MouseButtons) (modifiers: Keys) =
    let name =
        match button with
        | MouseButtons.Primary -> Some "MouseLeft"
        | MouseButtons.Alternate -> Some "MouseRight"
        | MouseButtons.Middle -> Some "MouseMiddle"
        | _ -> None

    name |> Option.map (chord_name (modifier_names modifiers))

let private win_key_down (key: System.Windows.Forms.Keys) = Win32.GetAsyncKeyState(int key) < 0s

let private win_modifier_names () =
    [ if
          win_key_down System.Windows.Forms.Keys.LControlKey
          || win_key_down System.Windows.Forms.Keys.RControlKey
      then
          "Control"
      if
          win_key_down System.Windows.Forms.Keys.LMenu
          || win_key_down System.Windows.Forms.Keys.RMenu
      then
          "Alt"
      if
          win_key_down System.Windows.Forms.Keys.LShiftKey
          || win_key_down System.Windows.Forms.Keys.RShiftKey
      then
          "Shift" ]

let try_side_mouse_binding () =
    let name =
        if win_key_down System.Windows.Forms.Keys.XButton1 then
            Some "MouseX1"
        elif win_key_down System.Windows.Forms.Keys.XButton2 then
            Some "MouseX2"
        else
            None

    name |> Option.map (chord_name (win_modifier_names ()))
