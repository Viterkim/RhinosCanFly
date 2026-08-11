module RhinosCanFly.FlightSpeed

open System
open Rhino

[<Literal>]
let documentSection = "RhinosCanFly"

[<Literal>]
let documentEntry = "FlyingSpeed"

type SessionSpeed =
    { document_serial_number: uint32 option
      value: float }

let mutable sessionSpeed: SessionSpeed option = None

let document_serial_number (document: RhinoDoc) =
    if isNull document then
        None
    else
        Some document.RuntimeSerialNumber

let try_document_speed (document: RhinoDoc) =
    if isNull document then
        None
    else
        document.Strings.GetValue(documentSection, documentEntry)
        |> Option.ofObj
        |> Option.bind Speed.try_parse

let current (document: RhinoDoc) (loadFromDocument: bool) (range: SpeedRange) (fallback: float) =
    let documentSerialNumber = document_serial_number document

    let requestedSpeed =
        match sessionSpeed with
        | Some session when session.document_serial_number = documentSerialNumber -> session.value
        | _ when loadFromDocument -> try_document_speed document |> Option.defaultValue fallback
        | _ -> fallback

    Speed.allowed range requestedSpeed

let set (document: RhinoDoc) (saveToDocument: bool) (range: SpeedRange) (requestedSpeed: float) =
    let speed = Speed.allowed range requestedSpeed

    sessionSpeed <-
        Some
            { document_serial_number = document_serial_number document
              value = speed }

    try
        if saveToDocument && not (isNull document) then
            let value = Speed.format speed
            let existing = document.Strings.GetValue(documentSection, documentEntry)

            if not (String.Equals(existing, value, StringComparison.Ordinal)) then
                document.Strings.SetString(documentSection, documentEntry, value) |> ignore

                document.Modified <- true

        Ok speed
    with error ->
        Error $"Could not save flying speed to the document: {error.Message}"

let step (config: MovementConfig) (speed: float) (SpeedStepCount steps: SpeedStepCount) =
    let stepped = speed * Math.Pow(config.speed_step_multiplier, steps)

    let requested =
        if steps < 0. then Math.Floor stepped
        elif steps > 0. then Math.Ceiling stepped
        else speed

    Speed.allowed config.speed_range requested
