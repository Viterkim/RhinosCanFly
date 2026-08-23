module RhinosCanFly.Platform.Win.GestureNavigationTransitions

open System.Diagnostics
open System.Drawing
open Rhino.Display
open RhinosCanFly
open RhinosCanFly.Platform.Win.ViewNavigationTypes

let complete_view_latch (state: State) =
    let previous = state.view_latch
    state.view_latch <- NoViewLatch
    ViewNavigationState.complete_view_latch previous

let stop (state: State) =
    state.gesture_navigation <- NoGestureNavigation
    ViewNavigationState.stop_timer_if_idle state

let begin_navigation
    (state: State)
    (owner: GestureOwner)
    (host: ViewportHostIdentity)
    (mode: ViewNavigationMode)
    (lifetime: GestureLifetime)
    =
    let mutable canStart = true

    match state.gesture_navigation with
    | GestureNavigationActive current when current.owner = owner && current.lifetime = GestureLifetime.Toggle ->
        stop state
        canStart <- false
    | GestureNavigationActive _ -> stop state
    | NoGestureNavigation -> ()

    if not canStart then
        Ok()
    else
        match complete_view_latch state with
        | Error error -> Error error
        | Ok() ->
            match state.routing.prepare_navigation host mode with
            | Error error -> Error error
            | Ok prepared ->
                state.gesture_navigation <-
                    GestureNavigationActive
                        { owner = owner
                          host = prepared
                          mode = mode
                          lifetime = lifetime }

                ViewNavigationState.keep_timer_running state
                Ok()

let press
    (state: State)
    (owner: GestureOwner)
    (action: RoutedMouseAction)
    (host: ViewportHostIdentity)
    (screenPoint: Point)
    =
    match action with
    | RoutedMouseAction.Off -> Ok()
    | RoutedMouseAction.Retarget mode ->
        let view = RhinoView.FromRuntimeSerialNumber host.view_serial_number

        if isNull view then
            Error "The retarget viewport is unavailable."
        else
            let point = view.ActiveViewport.ScreenToClient screenPoint

            state.routing.retarget host { x = point.X; y = point.Y } mode
    | RoutedMouseAction.TogglePivot -> begin_navigation state owner host ViewNavigationMode.Pivot GestureLifetime.Toggle
    | RoutedMouseAction.HoldPivot -> begin_navigation state owner host ViewNavigationMode.Pivot GestureLifetime.Hold
    | RoutedMouseAction.TogglePan -> begin_navigation state owner host ViewNavigationMode.Pan GestureLifetime.Toggle
    | RoutedMouseAction.HoldPan -> begin_navigation state owner host ViewNavigationMode.Pan GestureLifetime.Hold

let release (state: State) (owner: GestureOwner) =
    match state.gesture_navigation with
    | GestureNavigationActive current when current.owner = owner && current.lifetime = GestureLifetime.Hold ->
        stop state
    | GestureNavigationActive _
    | NoGestureNavigation -> ()

let owner_button_down (owner: GestureOwner) =
    match owner with
    | GestureOwner.ModifiedRightClick -> Win32Native.GetAsyncKeyState Win32Native.VK_RBUTTON < 0s
    | GestureOwner.Mouse4 -> Win32Native.GetAsyncKeyState Win32Native.VK_XBUTTON1 < 0s
    | GestureOwner.Mouse5 -> Win32Native.GetAsyncKeyState Win32Native.VK_XBUTTON2 < 0s

let poll (state: State) =
    match state.gesture_navigation with
    | NoGestureNavigation -> ()
    | GestureNavigationActive session ->
        if ViewNavigationState.foreground_root_window () <> session.host.root_window then
            stop state
        elif session.lifetime = GestureLifetime.Hold && not (owner_button_down session.owner) then
            stop state

let press_or_log
    (state: State)
    (owner: GestureOwner)
    (action: RoutedMouseAction)
    (host: ViewportHostIdentity)
    (point: Point)
    =
    match press state owner action host point with
    | Ok() -> ()
    | Error error -> Debug.WriteLine $"RhinosCanFly mouse action: {error}"
