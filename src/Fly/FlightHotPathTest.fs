module RhinosCanFly.FlightHotPathTest

type Mode =
    | Normal
    | BasicFlightKeysOnly

let mutable mode = Normal

let current () = mode

let next () =
    mode <-
        match mode with
        | Normal -> BasicFlightKeysOnly
        | BasicFlightKeysOnly -> Normal

    mode

let description (value: Mode) =
    match value with
    | Normal -> "Normal"
    | BasicFlightKeysOnly -> "Basic flight keys only"
