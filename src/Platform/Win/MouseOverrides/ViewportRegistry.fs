module RhinosCanFly.Platform.Win.ViewportRegistry

// Rhino 9 deprecates GetViewList(bool, bool), but Rhino 7 has no replacement.
#nowarn "44"

open System
open System.Diagnostics
open Rhino
open Rhino.Display
open RhinosCanFly

type Callbacks =
    { hook_installed: unit -> bool
      ensure_ui_wake: unit -> unit
      active_navigation_host: unit -> ViewportHostIdentity voption
      request_navigation_exit: unit -> unit
      log_exception: string -> exn -> unit }

type State =
    { callbacks: Callbacks
      mutable viewports: RightClickTransitions.RightClickViewport array
      mutable create_subscribed: bool
      mutable destroy_subscribed: bool
      mutable application_initialized: EventHandler option
      mutable view_created: EventHandler<ViewEventArgs> option
      mutable view_destroyed: EventHandler<ViewEventArgs> option }

let view_matches_host (host: ViewportHostIdentity) (view: RhinoView) =
    let (ViewWindowHandle expectedWindow) = host.view_window
    let document = view.Document

    not (Object.ReferenceEquals(document, null))
    && view.RuntimeSerialNumber = host.view_serial_number
    && document.RuntimeSerialNumber = host.document_serial_number
    && view.Handle = expectedWindow
    && view.ActiveViewportID = host.viewport_id
    && MouseOverrideState.root_window view.Handle = host.root_window

let capture_host (view: RhinoView) =
    { view_serial_number = view.RuntimeSerialNumber
      document_serial_number = view.Document.RuntimeSerialNumber
      viewport_id = view.ActiveViewportID
      view_window = ViewWindowHandle view.Handle
      root_window = MouseOverrideState.root_window view.Handle }

let capture_viewport (view: RhinoView) : RightClickTransitions.RightClickViewport =
    { host = capture_host view
      name = view.ActiveViewport.Name
      is_perspective = view.ActiveViewport.IsPerspectiveProjection
      is_parallel = view.ActiveViewport.IsParallelProjection }

let update (state: State) (view: RhinoView) =
    let updated = capture_viewport view
    let mutable index = 0
    let mutable found = false

    while index < state.viewports.Length && not found do
        if state.viewports[index].host.view_serial_number = updated.host.view_serial_number then
            state.viewports[index] <- updated
            found <- true

        index <- index + 1

    if not found then
        state.viewports <- Array.append state.viewports [| updated |]

let refresh (state: State) =
    try
        let refreshed =
            RhinoDoc.OpenDocuments()
            |> Array.collect (fun (document: RhinoDoc) -> document.Views.GetViewList(true, true))
            |> Array.choose (fun (view: RhinoView) ->
                if isNull view || isNull view.Document || view.Handle = nativeint 0 then
                    None
                else
                    Some(capture_viewport view))

        state.viewports <- refreshed
        Ok()
    with error ->
        state.callbacks.log_exception "viewport window refresh" error
        Error $"Could not enumerate Rhino viewport windows: {error.Message}"

let refresh_active (state: State) =
    try
        let document = RhinoDoc.ActiveDoc

        if not (isNull document) then
            let view = document.Views.ActiveView

            if not (isNull view) && view.Handle <> nativeint 0 then
                update state view
    with error ->
        state.callbacks.log_exception "active viewport refresh" error

let create (callbacks: Callbacks) =
    let state =
        { callbacks = callbacks
          viewports = Array.empty
          create_subscribed = false
          destroy_subscribed = false
          application_initialized = None
          view_created = None
          view_destroyed = None }

    let applicationInitialized =
        EventHandler(fun (_: obj) (_: EventArgs) ->
            if callbacks.hook_installed () then
                callbacks.ensure_ui_wake ()

                match refresh state with
                | Ok() -> ()
                | Error error -> Debug.WriteLine $"RhinosCanFly initialized viewport refresh: {error}")

    let viewCreated =
        EventHandler<ViewEventArgs>(fun (_: obj) (event: ViewEventArgs) ->
            try
                let view = event.View

                if not (isNull view) && not (isNull view.Document) && view.Handle <> nativeint 0 then
                    update state view
            with error ->
                callbacks.log_exception "viewport window created" error)

    let viewDestroyed =
        EventHandler<ViewEventArgs>(fun (_: obj) (event: ViewEventArgs) ->
            try
                let view = event.View

                if not (isNull view) then
                    let serialNumber = view.RuntimeSerialNumber

                    match callbacks.active_navigation_host () with
                    | ValueSome host when host.view_serial_number = serialNumber ->
                        callbacks.request_navigation_exit ()
                    | ValueSome _
                    | ValueNone -> ()

                    state.viewports <-
                        state.viewports
                        |> Array.filter (fun (candidate: RightClickTransitions.RightClickViewport) ->
                            candidate.host.view_serial_number <> serialNumber)
            with error ->
                callbacks.log_exception "viewport window destroyed" error)

    state.application_initialized <- Some applicationInitialized
    state.view_created <- Some viewCreated
    state.view_destroyed <- Some viewDestroyed
    RhinoApp.Initialized.AddHandler applicationInitialized
    state

let subscribe (state: State) =
    try
        if not state.create_subscribed then
            match state.view_created with
            | Some handler ->
                RhinoView.Create.AddHandler handler
                state.create_subscribed <- true
            | None -> failwith "The viewport Create handler is unavailable."

        if not state.destroy_subscribed then
            match state.view_destroyed with
            | Some handler ->
                RhinoView.Destroy.AddHandler handler
                state.destroy_subscribed <- true
            | None -> failwith "The viewport Destroy handler is unavailable."

        match refresh state with
        | Ok() -> ()
        | Error error -> failwith error
    with error ->
        if state.create_subscribed then
            try
                match state.view_created with
                | Some handler -> RhinoView.Create.RemoveHandler handler
                | None -> ()

                state.create_subscribed <- false
            with cleanupError ->
                Debug.WriteLine $"RhinosCanFly Create subscription rollback: {cleanupError}"

        if state.destroy_subscribed then
            try
                match state.view_destroyed with
                | Some handler -> RhinoView.Destroy.RemoveHandler handler
                | None -> ()

                state.destroy_subscribed <- false
            with cleanupError ->
                Debug.WriteLine $"RhinosCanFly Destroy subscription rollback: {cleanupError}"

        state.viewports <- Array.empty
        raise error

let unsubscribe (state: State) =
    if state.create_subscribed || state.destroy_subscribed then
        let errors = ResizeArray<string>()

        if state.create_subscribed then
            try
                match state.view_created with
                | Some handler -> RhinoView.Create.RemoveHandler handler
                | None -> ()

                state.create_subscribed <- false
            with error ->
                errors.Add $"Create: {error.Message}"

        if state.destroy_subscribed then
            try
                match state.view_destroyed with
                | Some handler -> RhinoView.Destroy.RemoveHandler handler
                | None -> ()

                state.destroy_subscribed <- false
            with error ->
                errors.Add $"Destroy: {error.Message}"

        state.viewports <- Array.empty

        if errors.Count > 0 then
            failwith (String.concat "; " errors)

let subscribed (state: State) =
    state.create_subscribed || state.destroy_subscribed

let try_viewport (state: State) (window: nativeint) =
    let mutable index = 0
    let mutable result = ValueNone

    while index < state.viewports.Length && ValueOption.isNone result do
        let candidate = state.viewports[index]
        let (ViewWindowHandle candidateWindow) = candidate.host.view_window

        if
            Win32Native.IsWindow candidateWindow
            && Win32Native.IsWindowEnabled candidateWindow
            && (candidateWindow = window || Win32Native.IsChild(candidateWindow, window))
        then
            result <- ValueSome candidate

        index <- index + 1

    result

let capture_allows_host (state: State) (host: ViewportHostIdentity) =
    let captureWindow = Win32Native.GetCapture()

    if captureWindow = nativeint 0 then
        true
    else
        match try_viewport state captureWindow with
        | ValueSome captureViewport -> MouseOverrideState.same_host captureViewport.host host
        | ValueNone -> false

let remove_application_handler (state: State) =
    match state.application_initialized with
    | Some handler ->
        RhinoApp.Initialized.RemoveHandler handler
        state.application_initialized <- None
    | None -> ()
