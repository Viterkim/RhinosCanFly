module RhinosCanFly.NavigationLoop

open Rhino

// Process ready input before pumping Rhino.
let run (is_running: unit -> bool) (work_pending: unit -> bool) (wait_timeout: unit -> int) (step: unit -> unit) =
    while is_running () do
        let mutable pump_after_input = work_pending ()

        if not pump_after_input then
            PlatformInput.wait_for_input_for (wait_timeout ())
            pump_after_input <- work_pending ()

        if not pump_after_input then
            RhinoApp.Wait()

        if is_running () then
            step ()

        // Finish the UI boundary even when processing input ended the session.
        if pump_after_input then
            RhinoApp.Wait()
