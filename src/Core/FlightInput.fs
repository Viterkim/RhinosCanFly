module RhinosCanFly.FlightInput

let movement_active (input: InputSnapshot) =
    input.forward
    || input.backward
    || input.left
    || input.right
    || input.up
    || input.down
    || input.pivot_left
    || input.pivot_right

let pivot_direction (input: InputSnapshot) =
    (if input.pivot_right then 1 else 0) - if input.pivot_left then 1 else 0

let without_pivot (input: InputSnapshot) =
    { input with
        pivot_left = false
        pivot_right = false }
