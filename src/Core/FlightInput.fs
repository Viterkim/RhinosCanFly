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
    match input.pivot_left, input.pivot_right with
    | true, false -> PivotLeft
    | false, true -> PivotRight
    | false, false
    | true, true -> NoPivot

let without_pivot (input: InputSnapshot) =
    { input with
        pivot_left = false
        pivot_right = false }
