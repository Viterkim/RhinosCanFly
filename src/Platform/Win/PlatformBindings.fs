module RhinosCanFly.PlatformBindings

open RhinosCanFly.Platform.Win

let parse (source: string) = KeyBindings.parse source

let is_down (binding: KeyBinding) = KeyBindings.is_down binding
