module RhinosCanFly.Runtime

open System
open System.Diagnostics
open Rhino
open Rhino.ApplicationSettings
open Rhino.Display
open Rhino.Geometry

[<Literal>]
let documentSpeedSection = "RhinosCanFly"

[<Literal>]
let documentSpeedEntry = "FlyingSpeed"

let down (key: KeyBinding) = KeyBindings.is_down key

let opt (key: KeyBinding option) =
    key |> Option.map down |> Option.defaultValue false

type private SessionSpeed =
    { document_serial_number: uint32 option
      value: float }

let mutable private sessionSpeed: SessionSpeed option = None
let mutable sessionRunning = false

let is_running () = sessionRunning

let viewport_gesture_active (view: RhinoView) = view.MouseCaptured(false)

let wait_for_viewport_gesture (view: RhinoView) =
    while viewport_gesture_active view do
        match Win32.wait_for_input Win32.INFINITE with
        | Ok() -> RhinoApp.Wait()
        | Error error -> failwith error

let document_serial_number (document: RhinoDoc) =
    if isNull document then
        None
    else
        Some document.RuntimeSerialNumber

let try_document_speed (document: RhinoDoc) =
    if isNull document then
        None
    else
        document.Strings.GetValue(documentSpeedSection, documentSpeedEntry)
        |> Option.ofObj
        |> Option.bind Speed.try_parse

let current_speed
    (document: RhinoDoc)
    (loadFromDocument: bool)
    (minimumSpeed: float)
    (maximumSpeed: float)
    (fallback: float)
    =
    let documentSerialNumber = document_serial_number document

    let sessionValue =
        sessionSpeed |> Option.map (fun (session: SessionSpeed) -> session.value)

    let requestedSpeed =
        match sessionSpeed with
        | Some session when session.document_serial_number = documentSerialNumber -> session.value
        | _ when loadFromDocument ->
            try_document_speed document
            |> Option.orElse sessionValue
            |> Option.defaultValue fallback
        | _ -> sessionValue |> Option.defaultValue fallback

    Speed.allowed minimumSpeed maximumSpeed requestedSpeed

let set_speed
    (document: RhinoDoc)
    (saveToDocument: bool)
    (minimumSpeed: float)
    (maximumSpeed: float)
    (requestedSpeed: float)
    =
    let speed = Speed.allowed minimumSpeed maximumSpeed requestedSpeed

    sessionSpeed <-
        Some
            { document_serial_number = document_serial_number document
              value = speed }

    try
        if saveToDocument && not (isNull document) then
            let value = Speed.format speed
            let existing = document.Strings.GetValue(documentSpeedSection, documentSpeedEntry)

            if not (String.Equals(existing, value, StringComparison.Ordinal)) then
                document.Strings.SetString(documentSpeedSection, documentSpeedEntry, value)
                |> ignore

                document.Modified <- true

        Ok speed
    with error ->
        Error $"Could not save flying speed to the document: {error.Message}"

let speed_step (state: FlyState) (direction: float) =
    let requestedSpeed =
        state.speed * Math.Pow(state.config.speed_step_multiplier, direction)

    state.speed <- Speed.allowed state.config.minimum_speed state.config.maximum_speed requestedSpeed

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

let drain_mouse_input (state: FlyState) =
    let dx, dy = state.mouse_dx, state.mouse_dy
    state.mouse_dx <- 0L
    state.mouse_dy <- 0L
    dx, dy

let apply_mouse_look (state: FlyState) =
    let dx, dy = drain_mouse_input state

    if dx = 0L && dy = 0L then
        false
    else
        state.camera <- Movement.look state.config dx dy state.camera
        true

let read_movement_input (state: FlyState) =

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
      mouse_dx = 0L
      mouse_dy = 0L }

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

let movement_active (input: InputSnapshot) =
    input.forward
    || input.backward
    || input.left
    || input.right
    || input.up
    || input.down

let poll_controls (state: FlyState) =
    if Win32.GetForegroundWindow() <> state.root_window || down state.config.exit_key then
        state.running <- false
        None
    else
        if state.config.wheel_changes_speed then
            let wheel = state.wheel_delta
            state.wheel_delta <- 0

            if wheel <> 0 then
                speed_step state (float wheel / float Win32.WHEEL_DELTA)

        toggles state
        Some(read_movement_input state)

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
        current_speed
            view.Document
            config.load_speed_from_document
            config.minimum_speed
            config.maximum_speed
            config.base_speed
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
            wait_for_viewport_gesture view
            MouseButtonOverrides.suspend ()

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
                    let mutable previousFrame = clock.Elapsed.TotalSeconds
                    let mutable movementActive = false

                    while state.running do
                        if not movementActive then
                            match Win32.wait_for_input Win32.INFINITE with
                            | Ok() -> ()
                            | Error error -> failwith error

                        RhinoApp.Wait()

                        if state.running then
                            let mouseChanged = apply_mouse_look state

                            match poll_controls state with
                            | None -> ()
                            | Some input ->
                                let now = clock.Elapsed.TotalSeconds
                                let currentlyMoving = movement_active input

                                let movementChanged =
                                    if movementActive && currentlyMoving then
                                        let dt = min (now - previousFrame) 0.05
                                        state.camera <- Movement.step state.config input dt state.camera
                                        true
                                    else
                                        false

                                previousFrame <- now
                                movementActive <- currentlyMoving

                                if state.running && (mouseChanged || movementChanged) then
                                    apply state

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

                        match
                            set_speed
                                view.Document
                                state.config.save_speed_to_document
                                state.config.minimum_speed
                                state.config.maximum_speed
                                state.speed
                        with
                        | Ok _ -> ()
                        | Error error -> RhinoApp.WriteLine $"RhinosCanFly: {error}"
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
            MouseButtonOverrides.resume ()
