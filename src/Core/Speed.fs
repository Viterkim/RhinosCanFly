module RhinosCanFly.Speed

open System
open System.Globalization

let allowed (minimumSpeed: float) (maximumSpeed: float) (requestedSpeed: float) =
    let finiteSpeed =
        if Double.IsNaN requestedSpeed || Double.IsNegativeInfinity requestedSpeed then
            minimumSpeed
        elif Double.IsPositiveInfinity requestedSpeed then
            maximumSpeed
        else
            requestedSpeed

    finiteSpeed |> Math.Ceiling |> max minimumSpeed |> min maximumSpeed

let try_parse (text: string) =
    if String.IsNullOrWhiteSpace text then
        None
    else
        let mutable speed = 0.

        if
            Double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, &speed)
            && not (Double.IsNaN speed)
            && not (Double.IsInfinity speed)
        then
            Some speed
        else
            None

let format (speed: float) =
    speed.ToString("R", CultureInfo.InvariantCulture)
