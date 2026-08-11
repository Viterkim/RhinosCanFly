module RhinosCanFly.FlightInput

let movement_active (input: InputSnapshot) =
    input.forward
    || input.backward
    || input.left
    || input.right
    || input.up
    || input.down
    || input.key_pivot_left
    || input.key_pivot_right

let key_pivot_direction (input: InputSnapshot) =
    match input.key_pivot_left, input.key_pivot_right with
    | true, false -> KeyPivotLeft
    | false, true -> KeyPivotRight
    | false, false
    | true, true -> NoKeyPivot

let without_key_pivot (input: InputSnapshot) =
    { input with
        key_pivot_left = false
        key_pivot_right = false }
