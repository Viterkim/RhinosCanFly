module RhinosCanFly.FlightInput

let movement_active (input: InputSnapshot) =
    input.forward <> input.backward
    || input.left <> input.right
    || input.up <> input.down
    || input.key_pivot_left <> input.key_pivot_right

let key_pivot_direction (input: InputSnapshot) =
    if input.key_pivot_left = input.key_pivot_right then
        NoKeyPivot
    elif input.key_pivot_left then
        KeyPivotLeft
    else
        KeyPivotRight
