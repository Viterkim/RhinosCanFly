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

let timed_out (startedAt: int64) =
    let elapsedTicks = Stopwatch.GetTimestamp() - startedAt
    float elapsedTicks / float Stopwatch.Frequency >= TRANSITION_TIMEOUT_SECONDS

let process_hook_events (state: State) =
    let mutable processing = true

    while processing
          && state.lifecycle = Available
          && state.pending_side_button_events.Count > 0 do
        match state.pending_side_button_events.Peek() with
        | ButtonDown(button, host, point, startedAt) ->
            if timed_out startedAt then
                state.pending_side_button_events.Dequeue() |> ignore
            else
                match
                    GestureNavigationTransitions.press
                        state
                        (owner button)
                        (MouseOverrideState.action_for state button)
                        host
                        point
                with
                | GestureNavigationTransitions.Applied _ -> state.pending_side_button_events.Dequeue() |> ignore
                | GestureNavigationTransitions.Deferred -> processing <- false
                | GestureNavigationTransitions.Failed error ->
                    Debug.WriteLine $"RhinosCanFly mouse action: {error}"
                    state.pending_side_button_events.Dequeue() |> ignore
        | ButtonUp button ->
            state.pending_side_button_events.Dequeue() |> ignore
            GestureNavigationTransitions.release state (owner button)

    if state.pending_side_button_events.Count > 0 then
        MouseOverrideState.keep_timer_running state
    else
        MouseOverrideState.stop_timer_if_idle state

let poll (state: State) = GestureNavigationTransitions.poll state
