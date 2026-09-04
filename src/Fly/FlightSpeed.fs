module RhinosCanFly.FlightSpeed

open System
open System.Collections.Generic
open Rhino

[<Literal>]
let DOCUMENT_SECTION = "RhinosCanFly"

[<Literal>]
let DOCUMENT_ENTRY = "FlyingSpeed"

let session_speeds = Dictionary<uint32, float>()
let mutable no_document_session_speed: float option = None

let try_session_speed (document: RhinoDoc) =
    if isNull document then
        no_document_session_speed
    else
        match session_speeds.TryGetValue document.RuntimeSerialNumber with
        | true, value -> Some value
        | false, _ -> None

let remember_session_speed (document: RhinoDoc) (speed: float) =
    if isNull document then
        no_document_session_speed <- Some speed
    else
        session_speeds[document.RuntimeSerialNumber] <- speed

let try_document_speed (document: RhinoDoc) =
    if isNull document then
        None
    else
        document.Strings.GetValue(DOCUMENT_SECTION, DOCUMENT_ENTRY)
        |> Option.ofObj
        |> Option.bind Speed.try_parse

let current (document: RhinoDoc) (load_from_document: bool) (range: SpeedRange) (fallback: float) =
    let requested_speed =
        match try_session_speed document with
        | Some speed -> speed
        | None when load_from_document -> try_document_speed document |> Option.defaultValue fallback
        | None -> fallback

    Speed.allowed range requested_speed

let set (document: RhinoDoc) (save_to_document: bool) (range: SpeedRange) (requested_speed: float) =
    let speed = Speed.allowed range requested_speed

    try
        if save_to_document && not (isNull document) then
            let value = Speed.format speed
            let existing = document.Strings.GetValue(DOCUMENT_SECTION, DOCUMENT_ENTRY)

            if not (String.Equals(existing, value, StringComparison.Ordinal)) then
                document.Strings.SetString(DOCUMENT_SECTION, DOCUMENT_ENTRY, value) |> ignore
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
            session_speeds.Remove event.Document.RuntimeSerialNumber |> ignore)

do RhinoDoc.CloseDocument.AddHandler document_closed

let shutdown () =
    RhinoDoc.CloseDocument.RemoveHandler document_closed
    session_speeds.Clear()
    no_document_session_speed <- None
