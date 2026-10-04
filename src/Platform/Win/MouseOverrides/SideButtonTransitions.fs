module RhinosCanFly.Platform.Win.SideButtonTransitions

open System.Diagnostics
open RhinosCanFly.Platform.Win.MouseOverrideTypes

let is_down (button: SideButton) =
    let key =
        match button with
        | Middle -> Win32Native.VK_MBUTTON
        | Mouse4 -> Win32Native.VK_XBUTTON1
        | Mouse5 -> Win32Native.VK_XBUTTON2

    Win32Native.GetAsyncKeyState key < 0s

let owner (button: SideButton) =
    match button with
    | Middle -> GestureOwner.Middle
    | Mouse4 -> GestureOwner.Mouse4
    | Mouse5 -> GestureOwner.Mouse5

let process_hook_events_with (handle: SideButtonHookEvent -> GestureNavigationTransitions.PressResult) (state: State) =
    if not state.processing_side_buttons then
        state.processing_side_buttons <- true

        try
            let mutable processing = true

            while processing
                  && state.lifecycle = Available
                  && state.pending_side_button_events.Count > 0 do
                let node = state.pending_side_button_events.First
                let result = handle node.Value

                match result with
                | GestureNavigationTransitions.Deferred -> processing <- false
                | _ ->
                    if not (isNull node.List) then
                        state.pending_side_button_events.Remove node

                    match result with
                    | GestureNavigationTransitions.Failed error -> Debug.WriteLine $"RhinosCanFly mouse action: {error}"
                    | _ -> ()
        finally
            state.processing_side_buttons <- false

    if state.pending_side_button_events.Count > 0 then
        MouseOverrideState.keep_timer_running state
    else
        MouseOverrideState.stop_timer_if_idle state

let process_hook_events (state: State) =
    if state.pending_side_button_events.Count = 0 then
        MouseOverrideState.stop_timer_if_idle state
    else
        process_hook_events_with
            (fun (event: SideButtonHookEvent) ->
                match event with
                | ButtonDown(button, host, point, admission) ->
                    GestureNavigationTransitions.press
                        state
                        (owner button)
                        (MouseOverrideState.action_for state button)
                        host
                        point
                        admission
                | ButtonUp button ->
                    GestureNavigationTransitions.release state (owner button)
                    GestureNavigationTransitions.Applied)
            state
