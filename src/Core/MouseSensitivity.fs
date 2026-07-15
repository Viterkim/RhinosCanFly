module RhinosCanFly.MouseSensitivity

let to_runtime (ConfigMouseSensitivity value: ConfigMouseSensitivity) = RuntimeMouseSensitivity(value / 10000.)
let radians_per_count (RuntimeMouseSensitivity value: RuntimeMouseSensitivity) = value
