module RhinosCanFly.MouseSensitivity

[<Literal>]
let SENSITIVITY_SCALE = 10000.

let to_radians_per_count (MouseSensitivitySetting value: MouseSensitivitySetting) =
    MouseRadiansPerCount(value / SENSITIVITY_SCALE)

let radians_per_count (MouseRadiansPerCount value: MouseRadiansPerCount) = value
