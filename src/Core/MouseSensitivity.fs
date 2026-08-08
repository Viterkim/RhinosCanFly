module RhinosCanFly.MouseSensitivity

[<Literal>]
let sensitivity_scale = 10000.

let to_runtime (ConfigMouseSensitivity value: ConfigMouseSensitivity) =
    RuntimeMouseSensitivity(value / sensitivity_scale)

let radians_per_count (RuntimeMouseSensitivity value: RuntimeMouseSensitivity) = value
