module RhinosCanFly.PlatformBindings

open System
open Eto.Forms
open RhinosCanFly.Platform.Win

let empty: KeyBinding =
    { virtual_keys = [||]
      unsupported = None }

let capture_names = BindingNames.capture_names

let capture_key_value (key: Keys) = key

let capture_mouse (buttons: MouseButtons) (modifiers: Keys) =
    struct (BindingNames.binding_from_mouse capture_names buttons modifiers, buttons = MouseButtons.Primary)

let parse (source: string) =
    BindingNames.parse source
    |> Result.map (fun (tokens: BindingToken array) ->
        let unsupported =
            tokens
            |> Array.tryFind (function
                | NamedKey name -> not (BindingNames.aliases.ContainsKey name)
                | WindowsVirtualKey _ -> false)

        { unsupported = unsupported
          virtual_keys =
            if Option.isSome unsupported then
                [||]
            else
                tokens
                |> Array.map (fun (token: BindingToken) ->
                    match token with
                    | NamedKey name -> VirtualKey BindingNames.aliases[name]
                    | WindowsVirtualKey code -> VirtualKey code)
                |> Array.distinct })

let execution_error (binding: KeyBinding) =
    binding.unsupported
    |> Option.map (function
        | NamedKey name -> $"'{name}' has no Windows key mapping. Choose another binding on Windows."
        | WindowsVirtualKey code -> $"0x{code:X2} has no Windows key mapping.")

let is_down (binding: KeyBinding) =
    let keys = binding.virtual_keys
    let mutable index = 0
    let mutable down = keys.Length > 0

    while down && index < keys.Length do
        let (VirtualKey key) = keys[index]
        down <- Win32.key_down key
        index <- index + 1

    down

let win_key_down (virtual_key: int) = Win32.key_down virtual_key

let win_modifier_names () =
    [ if win_key_down Win32Native.VK_LCONTROL || win_key_down Win32Native.VK_RCONTROL then
          "Control"
      if win_key_down Win32Native.VK_LMENU || win_key_down Win32Native.VK_RMENU then
          "Alt"
      if win_key_down Win32Native.VK_LSHIFT || win_key_down Win32Native.VK_RSHIFT then
          "Shift" ]

let process_id =
    use current_process = System.Diagnostics.Process.GetCurrentProcess()
    uint32 current_process.Id

let try_side_mouse_binding () =
    let mutable foreground_process = 0u

    Win32Native.GetWindowThreadProcessId(Win32Native.GetForegroundWindow(), &foreground_process)
    |> ignore

    let name =
        if foreground_process <> process_id then
            None
        elif win_key_down Win32Native.VK_XBUTTON1 then
            Some "MouseX1"
        elif win_key_down Win32Native.VK_XBUTTON2 then
            Some "MouseX2"
        else
            None

    name |> Option.map (BindingNames.chord_name (win_modifier_names ()))
