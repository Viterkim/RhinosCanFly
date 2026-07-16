module RhinosCanFly.Runtime

open System
open System.Diagnostics
open System.Threading
open Rhino
open Rhino.ApplicationSettings
open Rhino.Display
open Rhino.Geometry

let clamp (a: float) (b: float) (x: float) = max a (min b x)
let down (key: KeyBinding) = KeyBindings.is_down key

let opt (key: KeyBinding option) =
    key |> Option.map down |> Option.defaultValue false

let mutable sessionSpeed: float option = None
let mutable sessionRunning = false

let is_running () = sessionRunning

let current_speed (fallback: float) =
    sessionSpeed |> Option.defaultValue fallback

let reset_session_speed (speed: float) =
    sessionSpeed <- Some(Math.Ceiling speed)

let speed_step (state: FlyState) (direction: float) =
    let stepped = state.speed * Math.Pow(state.config.speed_step_multiplier, direction)
    state.speed <- clamp state.config.minimum_speed state.config.maximum_speed (Math.Ceiling stepped)

let toggles (state: FlyState) =
    let boost = down state.config.boost_toggle

    if
        not state.config.boost_hold_instead_of_toggle
        && boost
        && not state.boost_was_down
    then
        state.boost_enabled <- not state.boost_enabled

    state.boost_was_down <- boost

    let slow = down state.config.slow

    if not state.config.slow_hold_instead_of_toggle && slow && not state.slow_was_down then
        state.slow_enabled <- not state.slow_enabled

    state.slow_was_down <- slow

    let increase = opt state.config.speed_increase

    if increase && not state.speed_increase_was_down then
        speed_step state 1.

    state.speed_increase_was_down <- increase

    let decrease = opt state.config.speed_decrease

    if decrease && not state.speed_decrease_was_down then
        speed_step state -1.

    state.speed_decrease_was_down <- decrease

let read_input (state: FlyState) =
    let dx, dy = state.mouse_dx, state.mouse_dy
    state.mouse_dx <- 0L
    state.mouse_dy <- 0L

    let slow_active =
        if state.config.slow_hold_instead_of_toggle then
            down state.config.slow
        else
            state.slow_enabled

    let boost_active =
        if state.config.boost_hold_instead_of_toggle then
            down state.config.boost_toggle
        else
            state.boost_enabled

    let slow = if slow_active then state.config.slow_multiplier else 1.
    let boost = if boost_active then state.config.boost_multiplier else 1.

    { forward = down state.config.forward
      backward = down state.config.backward
      left = down state.config.left
      right = down state.config.right
      up = down state.config.up
      down = down state.config.down
      move_speed = state.speed * slow * boost
      mouse_dx = dx
      mouse_dy = dy }

let apply (state: FlyState) =
    let direction = Movement.direction_from_angles state.camera.yaw state.camera.pitch
    let target = state.camera.position + direction * state.target_distance
    state.viewport.CameraUp <- Vector3d.ZAxis

    // Rhino expects target first and camera location second.
    state.viewport.SetCameraLocations(target, state.camera.position) |> ignore
    state.view.Redraw()

let apply_entry_lens (state: FlyState) =
    let lens = state.config.lens_length_mm_in_mode

    if lens > 0. then
        state.viewport.Camera35mmLensLength <- lens
        state.view.Redraw()

let tick (state: FlyState) (dt: float) =
    if Win32.GetForegroundWindow() <> state.root_window || down state.config.exit_key then
        state.running <- false
    else
        if state.config.wheel_changes_speed then
            let wheel = state.wheel_delta
            state.wheel_delta <- 0

            if wheel <> 0 then
                speed_step state (float wheel / float Win32.WHEEL_DELTA)

        toggles state
        let input = read_input state

        if
            input.mouse_dx <> 0L
            || input.mouse_dy <> 0L
            || input.forward
            || input.backward
            || input.left
            || input.right
            || input.up
            || input.down
        then
            state.camera <- Movement.step state.config input dt state.camera
            apply state

let make_state (view: RhinoView) (config: FlyConfig) =
    let viewport = view.ActiveViewport

    let original_cursor =
        match Win32.get_cursor_position () with
        | Ok point -> point
        | Error error -> failwith error

    let yaw, pitch = Movement.angles_from_direction viewport.CameraDirection

    let target_distance =
        max 0.001 (viewport.CameraLocation.DistanceTo viewport.CameraTarget)

    let ancestor = Win32.GetAncestor(view.Handle, Win32.GA_ROOT)

    let root_window =
        if ancestor = nativeint 0 then
            Win32.GetForegroundWindow()
        else
            ancestor

    { view = view
      viewport = viewport
      config = config
      root_window = root_window
      original_cursor = original_cursor
      original_lens_length = viewport.Camera35mmLensLength
      target_distance = target_distance
      running = true
      camera =
        { position = viewport.CameraLocation
          yaw = yaw
          pitch = pitch }
      speed =
        clamp
            config.minimum_speed
            config.maximum_speed
            (sessionSpeed |> Option.defaultValue config.base_speed |> Math.Ceiling)
      mouse_dx = 0L
      mouse_dy = 0L
      wheel_delta = 0
      boost_enabled = false
      boost_was_down = down config.boost_toggle
      slow_enabled = false
      slow_was_down = down config.slow
      speed_increase_was_down = opt config.speed_increase
      speed_decrease_was_down = opt config.speed_decrease }

let run (view: RhinoView) (config: FlyConfig) =
    if sessionRunning then
        Error "Fly mode is already running."
    else
        sessionRunning <- true

        try
            try
                let state = make_state view config
                let originalTooltipsEnabled = CursorTooltipSettings.TooltipsEnabled
                let mutable raw = None
                let mutable captured = false
                let mutable cursorHidden = false
                let mutable tooltipsChanged = false

                try
                    CursorTooltipSettings.TooltipsEnabled <- false
                    tooltipsChanged <- true
                    Win32.clear_mouse_hover view.Handle
                    RhinoApp.Wait()
                    apply_entry_lens state

                    let rectangle = view.ScreenRectangle

                    match Win32.clip_cursor rectangle with
                    | Ok() -> captured <- true
                    | Error error -> failwith error

                    Win32.SetFocus view.Handle |> ignore
                    raw <- Some(new RawInputWindow(view.Handle, state))
                    Win32.ShowCursor false |> ignore
                    cursorHidden <- true
                    let clock = Stopwatch.StartNew()
                    let interval = 1. / config.update_hz
                    let mutable previous = clock.Elapsed.TotalSeconds

                    while state.running do
                        RhinoApp.Wait()
                        let now = clock.Elapsed.TotalSeconds

                        if now - previous >= interval then
                            let dt = min (now - previous) 0.05
                            previous <- now
                            tick state dt
                        else
                            Thread.Sleep 1

                    Ok()
                finally
                    try
                        match raw with
                        | Some window -> (window :> IDisposable).Dispose()
                        | None -> ()

                        Win32.clear_cursor_clip () |> ignore

                        if captured then
                            Win32.set_cursor_position state.original_cursor |> ignore

                        if cursorHidden then
                            Win32.ShowCursor true |> ignore

                        state.viewport.Camera35mmLensLength <- state.original_lens_length
                        sessionSpeed <- Some state.speed
                    finally
                        try
                            if tooltipsChanged then
                                CursorTooltipSettings.TooltipsEnabled <- originalTooltipsEnabled
                        finally
                            view.Redraw()
            with error ->
                Error error.Message
        finally
            sessionRunning <- false
