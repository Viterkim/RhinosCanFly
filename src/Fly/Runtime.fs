module RhinosCanFly.Runtime

open Rhino.Display

let is_running () = FlightSession.is_running ()

let can_start () = FlightSession.can_start ()

let state_name () = FlightSession.state_name ()

let recovery_completed () = FlightSession.recovery_completed ()

let viewport_gesture_active (view: RhinoView) =
    FlightSession.viewport_gesture_active view

let run (view: RhinoView) (config: FlyConfig) (sessionMode: FlightSessionMode) =
    FlightSession.run view config sessionMode
