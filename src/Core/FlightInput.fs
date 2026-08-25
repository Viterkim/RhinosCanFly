module RhinosCanFly.FlightInput

let movement_active (input: FlightMovementInput) =
    input.forward <> input.backward
    || input.left <> input.right
    || input.up <> input.down
    || input.key_pivot_left <> input.key_pivot_right

let key_pivot_direction (input: FlightMovementInput) =
    if input.key_pivot_left = input.key_pivot_right then
        NoKeyPivot
    elif input.key_pivot_left then
        KeyPivotLeft
    else
        KeyPivotRight
