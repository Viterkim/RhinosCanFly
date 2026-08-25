namespace RhinosCanFly

type FlightExitReason = SessionFailure of string

type RawMouseButtonEvent =
    | None = 0
    | MiddleDown = 5
    | MiddleUp = 6

[<Struct>]
type RawMouseButtonTransition =
    { event: RawMouseButtonEvent
      timestamp: int64 }
