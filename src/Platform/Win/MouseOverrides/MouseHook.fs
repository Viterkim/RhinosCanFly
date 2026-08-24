module RhinosCanFly.Platform.Win.MouseHook

[<Literal>]
let MAXIMUM_REMOVAL_ATTEMPTS = 8

type Status =
    | Absent
    | Installed of Win32Native.WindowsHook
    | RemovalPending of hook: Win32Native.WindowsHook * error: string * attempts: int
    | RemovalAbandoned of hook: Win32Native.WindowsHook * error: string

type State = { mutable status: Status }

type Environment =
    { handle_event: Win32.MouseHookEvent -> bool
      subscribe_viewports: unit -> unit
      unsubscribe_viewports: unit -> unit
      viewports_subscribed: unit -> bool
      install_ui_wake: unit -> unit
      remove_ui_wake: unit -> unit
      keep_watchdog_running: unit -> unit
      log_exception: string -> exn -> unit }

let create () = { status = Absent }

let installed (state: State) =
    match state.status with
    | Installed _ -> true
    | Absent
    | RemovalPending _
    | RemovalAbandoned _ -> false

let absent (state: State) =
    match state.status with
    | Absent -> true
    | Installed _
    | RemovalPending _
    | RemovalAbandoned _ -> false

let removal_pending (state: State) =
    match state.status with
    | RemovalPending _ -> true
    | Absent
    | Installed _
    | RemovalAbandoned _ -> false

let removal_abandoned (state: State) =
    match state.status with
    | RemovalAbandoned _ -> true
    | Absent
    | Installed _
    | RemovalPending _ -> false

let removal_failed (state: State) =
    match state.status with
    | RemovalPending _
    | RemovalAbandoned _ -> true
    | Absent
    | Installed _ -> false

let install (state: State) (environment: Environment) =
    match state.status with
    | Installed _ -> Ok()
    | RemovalPending(_, error, _)
    | RemovalAbandoned(_, error) -> Error $"The previous mouse hook could not be removed: {error}"
    | Absent ->
        try
            environment.subscribe_viewports ()

            match Win32.install_mouse_hook environment.handle_event with
            | Ok hook ->
                state.status <- Installed hook
                environment.install_ui_wake ()
                Ok()
            | Error error ->
                environment.unsubscribe_viewports ()
                Error error
        with error ->
            environment.log_exception "mouse-hook installation" error
            Error $"Could not track Rhino viewport windows: {error.Message}"

let remove (state: State) (environment: Environment) =
    match state.status with
    | Absent ->
        try
            environment.unsubscribe_viewports ()
            environment.remove_ui_wake ()
            Ok()
        with error ->
            environment.log_exception "mouse-hook removal" error
            Error $"Could not stop tracking Rhino viewport windows: {error.Message}"
    | Installed hook ->
        match Win32.remove_hook hook with
        | Ok() ->
            state.status <- Absent
            environment.remove_ui_wake ()

            try
                environment.unsubscribe_viewports ()
                Ok()
            with error ->
                Error $"Could not stop tracking Rhino viewport windows: {error.Message}"
        | Error error ->
            state.status <- RemovalPending(hook, error, 1)
            environment.keep_watchdog_running ()
            Error error
    | RemovalPending(hook, _, _)
    | RemovalAbandoned(hook, _) ->
        match Win32.remove_hook hook with
        | Ok() ->
            state.status <- Absent
            environment.remove_ui_wake ()

            try
                environment.unsubscribe_viewports ()
                Ok()
            with error ->
                Error $"Could not stop tracking Rhino viewport windows: {error.Message}"
        | Error error ->
            let nextAttempt =
                match state.status with
                | RemovalPending(_, _, previousAttempts) -> previousAttempts + 1
                | RemovalAbandoned _ -> MAXIMUM_REMOVAL_ATTEMPTS
                | Absent
                | Installed _ -> 1

            if nextAttempt >= MAXIMUM_REMOVAL_ATTEMPTS then
                state.status <- RemovalAbandoned(hook, error)
            else
                state.status <- RemovalPending(hook, error, nextAttempt)
                environment.keep_watchdog_running ()

            Error error

let refresh (state: State) (environment: Environment) (needed: bool) =
    if needed then
        install state environment
    else
        remove state environment

let needs_reconciliation (state: State) (environment: Environment) (needed: bool) =
    match state.status with
    | Absent -> needed || environment.viewports_subscribed ()
    | Installed _ -> not needed
    | RemovalPending _ -> true
    | RemovalAbandoned _ -> false
