module RhinosCanFly.PlatformMouseActions

#nowarn "44"

open System
open System.Collections.Generic
open System.Diagnostics
open Rhino
open Rhino.Commands
open Rhino.Display
open RhinosCanFly.Platform.Mac

type Action =
    | Flight of FlightMode * held: bool
    | Navigate of ViewportNavigation.Operation * held: bool
    | Retarget of RetargetMode

type Request =
    { host: ViewportHostIdentity
      point: ViewportClientPoint
      button: int
      pair: int64
      mutable deadline: int64
      action: Action
      use_cursor: bool }

type CachedView =
    { view: RhinoView
      mutable host: ViewportHostIdentity
      mutable name: string
      mutable is_parallel: bool
      mutable captured: bool
      mutable left: float
      mutable top: float
      mutable width: float
      mutable height: float
      mutable client_width: float
      mutable client_height: float }

let suspended = HashSet<int64>()
let mutable next_suspension_id = 0L
let mutable routing: MouseOverrideConfig option = None
let pending = Array.create<Request option> 5 None
let pairs = Array.zeroCreate<int64> 5
let released = Array.create 5 true
let mutable views: CachedView array = [||]
let mutable dirty = true
let mutable next_resolution_at = 0L
let mutable command_active = false
let mutable dispatch: Request -> unit = ignore
let mutable subscribed = false
let mutable navigation_running: unit -> bool = fun () -> false

let held (request: Request) =
    pairs[request.button] = request.pair
    && not released[request.button]
    && (match MacNavigationInput.current with
        | Some session when session.active && session.ready && session.worker ->
            PlatformFlightKeyboard.key_is_down (128 + request.button)
        | _ -> MacNative.mouse_down request.button)

let clear_pending () =
    Array.fill pending 0 pending.Length None
    MouseFlightEntry.revoke ()

    for button = 0 to pairs.Length - 1 do
        pairs[button] <- pairs[button] + 1L

let clear_window_pending (window: nativeint) =
    if window = 0n then
        clear_pending ()
    else
        MouseFlightEntry.revoke ()

        for index = 0 to pending.Length - 1 do
            let pending_in_window =
                pending[index]
                |> Option.exists (fun (request: Request) -> request.host.window = window)

            if pending_in_window then
                pending[index] <- None
                pairs[index] <- pairs[index] + 1L

let refresh () =
    let now = Stopwatch.GetTimestamp()

    if dirty && now >= next_resolution_at then
        let document = RhinoDoc.ActiveDoc

        views <-
            if isNull document then
                [||]
            else
                // Rhino 8 still uses this overload.
                document.Views.GetViewList(true, true)
                |> Array.choose (fun (view: RhinoView) ->
                    try
                        Some
                            { view = view
                              host = PlatformInput.capture_viewport_host view
                              name = ""
                              is_parallel = false
                              captured = true
                              left = 0.
                              top = 0.
                              width = 0.
                              height = 0.
                              client_width = 0.
                              client_height = 0. }
                    with _ ->
                        None)

        dirty <- views.Length = 0
        next_resolution_at <- now + Stopwatch.Frequency / 4L

    for cached in views do
        try
            let view = cached.view

            if
                cached.host.viewport_id <> view.ActiveViewportID
                || not (PlatformInput.viewport_host_windows_exist cached.host)
            then
                cached.host <- PlatformInput.capture_viewport_host view

            let viewport = view.ActiveViewport
            let bounds = viewport.Bounds
            let top_left = viewport.ClientToScreen(System.Drawing.Point(0, 0))

            let bottom_right =
                viewport.ClientToScreen(System.Drawing.Point(bounds.Width, bounds.Height))

            let width = float (bottom_right.X - top_left.X)
            let height = float (bottom_right.Y - top_left.Y)

            if bounds.Width <= 0 || bounds.Height <= 0 || not (width > 0.) || not (height > 0.) then
                invalidOp "The viewport has no usable screen rectangle."

            cached.left <- float top_left.X
            cached.top <- float top_left.Y
            cached.width <- width
            cached.height <- height
            cached.client_width <- float bounds.Width
            cached.client_height <- float bounds.Height
            cached.name <- viewport.Name
            cached.is_parallel <- viewport.IsParallelProjection
            cached.captured <- view.MouseCaptured false
        with _ ->
            cached.captured <- true
            dirty <- true

let routed (action: RoutedMouseAction) =
    match action with
    | RoutedMouseAction.TogglePivot -> Some(Navigate(ViewportNavigation.Operation.Pivot, false))
    | RoutedMouseAction.HoldPivot -> Some(Navigate(ViewportNavigation.Operation.Pivot, true))
    | RoutedMouseAction.TogglePan -> Some(Navigate(ViewportNavigation.Operation.Pan, false))
    | RoutedMouseAction.HoldPan -> Some(Navigate(ViewportNavigation.Operation.Pan, true))
    | RoutedMouseAction.StartFlight mode -> Some(Flight(mode, false))
    | RoutedMouseAction.Retarget mode -> Some(Retarget mode)
    | RoutedMouseAction.Off -> None

let action_for (config: MouseActionConfig) (cached: CachedView) (event: MacNative.InputEvent) =
    if not (ViewportNameList.allows cached.name config.viewport_capabilities) then
        None
    else
        let shift = event.modifiers &&& (1UL <<< 17) <> 0UL
        let control = event.modifiers &&& (1UL <<< 18) <> 0UL
        let alt = event.modifiers &&& (1UL <<< 19) <> 0UL

        if event.modifiers &&& (1UL <<< 20) <> 0UL then
            None
        else
            match int event.code with
            | 2 -> routed config.middle
            | 3 -> routed config.mouse4
            | 4 -> routed config.mouse5
            | 1 ->
                let gesture =
                    match shift, control, alt with
                    | true, false, false -> config.shift_right_click
                    | false, true, false -> config.ctrl_right_click
                    | false, false, true -> config.alt_right_click
                    | _ -> RoutedMouseAction.Off

                match routed gesture with
                | Some action -> Some action
                | None when cached.is_parallel && shift && not control && not alt ->
                    Some(Navigate(ViewportNavigation.Operation.ParallelPan, true))
                | None when cached.is_parallel && alt && not control && not shift ->
                    Some(Navigate(ViewportNavigation.Operation.ParallelZoom, true))
                | None when
                    not shift
                    && not control
                    && not alt
                    && ViewportNameList.allows cached.name config.right_click_flight_entry
                    ->
                    let mode = DefaultFlightMode.flight_mode config.default_flight_mode

                    match config.right_click_entry with
                    | RightClickEntryMode.ClickToFly when not command_active -> Some(Flight(mode, false))
                    | RightClickEntryMode.HoldToFly when not command_active -> Some(Flight(mode, true))
                    | RightClickEntryMode.ClickToFlyDuringCommands -> Some(Flight(mode, false))
                    | RightClickEntryMode.HoldToFlyDuringCommands -> Some(Flight(mode, true))
                    | _ -> None
                | _ -> None
            | _ -> None

let observe (event: MacNative.InputEvent) =
    let button = int event.code

    if event.kind = 6u then
        clear_window_pending event.window
        0u
    elif event.kind <> 4u || button < 0 || button >= pending.Length then
        0u
    elif event.down = 0u then
        released[button] <- true

        match pending[button] with
        | Some request when request.deadline = 0L ->
            request.deadline <- Stopwatch.GetTimestamp() + 2L * Stopwatch.Frequency
        | _ -> ()

        0u
    elif
        suspended.Count <> 0
        || event.content = 0u
        || MacNative.foreground_window () <> event.window
        || event.buttons &&& ~~~(1u <<< button) <> 0u
    then
        0u
    else
        let mutable result = 0u
        let mutable index = 0

        while result = 0u && index < views.Length do
            let cached = views[index]

            if
                not cached.captured
                && cached.host.window = event.window
                && event.screen_x >= cached.left
                && event.screen_x < cached.left + cached.width
                && event.screen_y >= cached.top
                && event.screen_y < cached.top + cached.height
            then
                match routing with
                | Some routing ->
                    match action_for routing.actions cached event with
                    | Some action ->
                        clear_pending ()
                        released[button] <- false

                        let use_cursor =
                            match button with
                            | 2 -> routing.actions.outside_flight_cursor.middle
                            | 3 -> routing.actions.outside_flight_cursor.mouse4
                            | 4 -> routing.actions.outside_flight_cursor.mouse5
                            | _ -> true

                        pending[button] <-
                            Some
                                { host = cached.host
                                  button = button
                                  pair = pairs[button]
                                  deadline =
                                    match action with
                                    | Flight(_, false) -> 0L
                                    | _ -> Stopwatch.GetTimestamp() + 2L * Stopwatch.Frequency
                                  action = action
                                  point =
                                    { x = int ((event.screen_x - cached.left) * cached.client_width / cached.width)
                                      y = int ((event.screen_y - cached.top) * cached.client_height / cached.height) }
                                  use_cursor = use_cursor }

                        result <- 1u
                    | None -> ()
                | None -> ()

            index <- index + 1

        result

let pending_is_current (request: Request) =
    pairs[request.button] = request.pair
    && (pending[request.button]
        |> Option.exists (fun (current: Request) -> obj.ReferenceEquals(current, request)))

let pulse () =
    let cleanup_error =
        try
            MacNavigationInput.complete_cleanup ()
        with error ->
            Some error.Message

    cleanup_error |> Option.iter (fun (error: string) -> Debug.WriteLine error)

    try
        MouseFlightEntry.poll ()

        let routing_usable =
            routing
            |> Option.exists (fun (config: MouseOverrideConfig) ->
                ViewportNameList.has_allowed_viewports config.actions.viewport_capabilities)

        if subscribed && suspended.Count = 0 && routing_usable then
            if not (navigation_running ()) then
                refresh ()

            command_active <- Command.InCommand()

            for button = 0 to pending.Length - 1 do
                match pending[button] with
                | Some request ->
                    if not (MacNative.mouse_down button) then
                        released[button] <- true

                    let ready =
                        match request.action with
                        | Flight(_, false) -> released[button]
                        | _ -> true

                    if ready && request.deadline = 0L then
                        request.deadline <- Stopwatch.GetTimestamp() + 2L * Stopwatch.Frequency

                    let unavailable =
                        not (PlatformInput.viewport_host_exists request.host request.host.view)
                        || not (PlatformInput.viewport_application_is_foreground request.host)
                        || (request.deadline <> 0L && Stopwatch.GetTimestamp() >= request.deadline)

                    if not (pending_is_current request) then
                        ()
                    elif unavailable then
                        pending[button] <- None
                    elif ready then
                        let still_held = held request

                        let cleanup_pending =
                            match request.action, MacNavigationInput.current with
                            | Retarget _, _ -> false
                            | _, Some session when not session.active -> MacNavigationInput.pending_cleanup ()
                            | _ -> false

                        let requires_hold =
                            match request.action with
                            | Flight(_, true)
                            | Navigate(_, true) -> true
                            | _ -> false

                        if requires_hold && not still_held then
                            pending[button] <- None
                        elif cleanup_pending then
                            ()
                        elif not (request.host.view.MouseCaptured false) && pending_is_current request then
                            pending[button] <- None
                            dispatch request
                | None -> ()

    with error ->
        try
            RhinoApp.WriteLine $"RhinosCanFly mouse action failed: {error.Message}"
        with output ->
            Debug.WriteLine output

let main_loop = EventHandler(fun (_: obj) (_: EventArgs) -> pulse ())

let view_changed =
    EventHandler<ViewEventArgs>(fun (_: obj) (_: ViewEventArgs) ->
        dirty <- true
        next_resolution_at <- 0L

        if Array.exists Option.isSome pending || MouseFlightEntry.has_pending () then
            clear_pending ()
        else
            MouseFlightEntry.revoke ())

let command_began =
    EventHandler<CommandEventArgs>(fun (_: obj) (event: CommandEventArgs) ->
        command_active <- true

        if event.CommandEnglishName <> "RhinosCanFlyMouseEntry" then
            clear_pending ())

let command_ended =
    EventHandler<CommandEventArgs>(fun (_: obj) (_: CommandEventArgs) -> command_active <- Command.InCommand())

let apply (config: MouseOverrideConfig) : Result<unit, string> =
    try
        clear_pending ()
        routing <- Some config
        dirty <- true
        next_resolution_at <- 0L

        MacNavigationInput.enable_outside (
            ViewportNameList.has_allowed_viewports config.actions.viewport_capabilities
            && suspended.Count = 0
        )

        Ok()
    with error ->
        Error error.Message

let prepare (dispatch_request: Request -> unit) (is_navigation_running: unit -> bool) =
    dispatch <- dispatch_request
    navigation_running <- is_navigation_running
    MacNavigationInput.outside_event <- observe

    if not subscribed then
        subscribed <- true
        RhinoApp.MainLoop.AddHandler main_loop
        RhinoView.Create.AddHandler view_changed
        RhinoView.Destroy.AddHandler view_changed
        RhinoView.SetActive.AddHandler view_changed
        Command.BeginCommand.AddHandler command_began
        Command.EndCommand.AddHandler command_ended

let suspend () =
    clear_pending ()
    MacNavigationInput.enable_outside false
    MacNavigationInput.request_stop FocusLost
    next_suspension_id <- next_suspension_id + 1L
    suspended.Add next_suspension_id |> ignore

    let cleanup_error =
        try
            MacNavigationInput.complete_cleanup ()
        with error ->
            Some error.Message

    Ok
        { id = next_suspension_id
          cleanup_error = cleanup_error }

let resume (lease: InputSuspensionLease) : Result<unit, string> =
    suspended.Remove lease.id |> ignore

    match routing with
    | Some config -> apply config
    | None -> Ok()

let consume_mouse_flight_entry (view: RhinoView) (request_id: Guid) =
    MouseFlightEntry.consume view request_id

let shutdown () =
    clear_pending ()
    suspended.Clear()
    routing <- None
    views <- [||]
    MacNavigationInput.enable_outside false
    MacNavigationInput.complete_cleanup () |> ignore

    if subscribed then
        subscribed <- false
        RhinoApp.MainLoop.RemoveHandler main_loop
        RhinoView.Create.RemoveHandler view_changed
        RhinoView.Destroy.RemoveHandler view_changed
        RhinoView.SetActive.RemoveHandler view_changed
        Command.BeginCommand.RemoveHandler command_began
        Command.EndCommand.RemoveHandler command_ended
