module RhinosCanFly.MouseSensitivity

[<Literal>]
let SENSITIVITY_SCALE = 10000.

let to_runtime (ConfigMouseSensitivity value: ConfigMouseSensitivity) =
    RuntimeMouseSensitivity(value / SENSITIVITY_SCALE)

let radians_per_count (RuntimeMouseSensitivity value: RuntimeMouseSensitivity) = value
