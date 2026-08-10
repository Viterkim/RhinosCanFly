namespace RhinosCanFly

[<Struct>]
type VirtualKey = VirtualKey of int

type KeyBinding = { virtual_keys: VirtualKey list }
