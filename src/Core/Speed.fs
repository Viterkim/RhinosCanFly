namespace RhinosCanFly

[<Struct>]
type SpeedRange = { minimum: float; maximum: float }

[<Struct>]
type SpeedStepCount = SpeedStepCount of float

module Speed =

    open System
    open System.Globalization

    let allowed (range: SpeedRange) (requested_speed: float) =
        let finite_speed =
            if Double.IsNaN requested_speed || Double.IsNegativeInfinity requested_speed then
                range.minimum
            elif Double.IsPositiveInfinity requested_speed then
                range.maximum
            else
                requested_speed

        finite_speed |> max range.minimum |> min range.maximum

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
