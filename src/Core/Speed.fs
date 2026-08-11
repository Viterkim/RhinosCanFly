namespace RhinosCanFly

[<Struct>]
type SpeedRange = { minimum: float; maximum: float }

[<Struct>]
type SpeedStepCount = SpeedStepCount of float

module Speed =

    open System
    open System.Globalization

    let allowed (range: SpeedRange) (requestedSpeed: float) =
        let finiteSpeed =
            if Double.IsNaN requestedSpeed || Double.IsNegativeInfinity requestedSpeed then
                range.minimum
            elif Double.IsPositiveInfinity requestedSpeed then
                range.maximum
            else
                requestedSpeed

        finiteSpeed |> Math.Ceiling |> max range.minimum |> min range.maximum

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
