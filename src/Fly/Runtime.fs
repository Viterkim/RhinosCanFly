module RhinosCanFly.Runtime

open Rhino.Display

let is_running () = FlightSession.is_running ()

let can_start () = FlightSession.can_start ()

let viewport_gesture_active (view: RhinoView) =
    FlightSession.viewport_gesture_active view

let release_viewport_gesture (view: RhinoView) =
    FlightSession.release_viewport_gesture view

let run (view: RhinoView) (config: FlyConfig) (sessionMode: FlightSessionMode) =
    FlightSession.run view config sessionMode
