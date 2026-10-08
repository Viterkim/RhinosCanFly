module RhinosCanFly.RelativeMotion

open System
open System.Diagnostics
open System.Runtime.InteropServices

[<Struct; StructLayout(LayoutKind.Sequential)>]
type Packet =
    val mutable timestamp: float
    val mutable dx: float
    val mutable dy: float

[<UnmanagedFunctionPointer(CallingConvention.Cdecl)>]
type Handler = delegate of packet: byref<Packet> -> unit

[<Struct>]
type Movement =
    { timestamp: int64
      dx: int64
      dy: int64 }

// One decoder per session, called serially by the native motion source.
type Decoder(timestamp_offset: int64 voption) =
    let mutable native_origin = ValueNone
    let mutable clock_origin = 0L
    let mutable previous_timestamp = 0L
    let mutable dx_remainder = 0.
    let mutable dy_remainder = 0.

    member _.Decode(packet: Packet) =
        let received_at = Stopwatch.GetTimestamp()
        let dx = dx_remainder + packet.dx
        let dy = dy_remainder + packet.dy

        if
            Double.IsNaN packet.timestamp
            || Double.IsInfinity packet.timestamp
            || packet.timestamp < 0.
            || Double.IsNaN dx
            || Double.IsInfinity dx
            || abs dx >= 4503599627370496.
            || Double.IsNaN dy
            || Double.IsInfinity dy
            || abs dy >= 4503599627370496.
        then
            invalidOp "The native mouse source returned invalid relative motion."

        let mapped =
            match timestamp_offset with
            | ValueSome value -> packet.timestamp * float Stopwatch.Frequency + float value
            | ValueNone ->
                // Wayland timestamps have no specified origin.
                let origin =
                    match native_origin with
                    | ValueSome value -> value
                    | ValueNone ->
                        native_origin <- ValueSome packet.timestamp
                        clock_origin <- received_at
                        packet.timestamp

                float clock_origin + (packet.timestamp - origin) * float Stopwatch.Frequency

        let timestamp =
            if mapped >= float received_at then received_at
            elif mapped <= 0. then 0L
            else int64 mapped

        previous_timestamp <- max previous_timestamp timestamp
        let whole_dx = int64 dx
        let whole_dy = int64 dy
        dx_remainder <- dx - float whole_dx
        dy_remainder <- dy - float whole_dy

        { timestamp = previous_timestamp
          dx = whole_dx
          dy = whole_dy }
