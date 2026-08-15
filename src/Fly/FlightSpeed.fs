module RhinosCanFly.FlightSpeed

open System
open System.Collections.Generic
open Rhino

[<Literal>]
let documentSection = "RhinosCanFly"

[<Literal>]
let documentEntry = "FlyingSpeed"

let sessionSpeeds = Dictionary<uint32, float>()
let mutable noDocumentSessionSpeed: float option = None

let try_session_speed (document: RhinoDoc) =
    if isNull document then
        noDocumentSessionSpeed
    else
        match sessionSpeeds.TryGetValue document.RuntimeSerialNumber with
        | true, value -> Some value
        | false, _ -> None

let remember_session_speed (document: RhinoDoc) (speed: float) =
    if isNull document then
        noDocumentSessionSpeed <- Some speed
    else
        sessionSpeeds[document.RuntimeSerialNumber] <- speed

let try_document_speed (document: RhinoDoc) =
    if isNull document then
        None
    else
        document.Strings.GetValue(documentSection, documentEntry)
        |> Option.ofObj
        |> Option.bind Speed.try_parse

let current (document: RhinoDoc) (loadFromDocument: bool) (range: SpeedRange) (fallback: float) =
    let requestedSpeed =
        match try_session_speed document with
        | Some speed -> speed
        | None when loadFromDocument -> try_document_speed document |> Option.defaultValue fallback
        | None -> fallback

    Speed.allowed range requestedSpeed

let set (document: RhinoDoc) (saveToDocument: bool) (range: SpeedRange) (requestedSpeed: float) =
    let speed = Speed.allowed range requestedSpeed

    try
        if saveToDocument && not (isNull document) then
            let value = Speed.format speed
            let existing = document.Strings.GetValue(documentSection, documentEntry)

            if not (String.Equals(existing, value, StringComparison.Ordinal)) then
                document.Strings.SetString(documentSection, documentEntry, value) |> ignore
                document.Modified <- true

        remember_session_speed document speed
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

let document_closed =
    EventHandler<DocumentEventArgs>(fun (_: obj) (event: DocumentEventArgs) ->
        if not (isNull event.Document) then
            sessionSpeeds.Remove event.Document.RuntimeSerialNumber |> ignore)

do RhinoDoc.CloseDocument.AddHandler document_closed

let shutdown () =
    RhinoDoc.CloseDocument.RemoveHandler document_closed
    sessionSpeeds.Clear()
    noDocumentSessionSpeed <- None
