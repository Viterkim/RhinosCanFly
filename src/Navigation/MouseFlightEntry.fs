module RhinosCanFly.MouseFlightEntry

open System
open System.Diagnostics
open Rhino.Display

type Permission =
    { mode: FlightMode
      held: (unit -> bool) option
      held_buttons: uint32
      entry_press: MouseEntryPress option
      valid: unit -> bool }

type Request =
    { id: Guid
      host: ViewportHostIdentity
      deadline: int64
      generation: int64
      invalidated: bool ref
      mutable focus_watch: IDisposable option
      is_current: unit -> bool
      mode: FlightMode
      held: (unit -> bool) option
      held_buttons: uint32
      entry_press: MouseEntryPress option }

let mutable pending: Request option = None
let mutable generation = 0L

let release_watch (request: Request) =
    let watch = request.focus_watch
    request.focus_watch <- None
    watch |> Option.iter (fun (observer: IDisposable) -> observer.Dispose())

let revoke () =
    let previous = pending
    generation <- generation + 1L
    pending <- None
    previous |> Option.iter release_watch

let cancel (id: Guid) =
    match pending with
    | Some request when request.id = id -> revoke ()
    | _ -> ()

let has_pending () = Option.isSome pending

let request_is_valid (request: Request) (view: RhinoView) =
    try
        not request.invalidated.Value
        && request.generation = generation
        && Stopwatch.GetTimestamp() < request.deadline
        && request.is_current ()
        && (request.held |> Option.forall (fun (held: unit -> bool) -> held ()))
        && PlatformInput.viewport_host_exists request.host view
        && PlatformInput.viewport_host_is_foreground request.host view
    with _ ->
        false

let poll () =
    match pending with
    | Some request ->
        let view =
            try
                RhinoView.FromRuntimeSerialNumber request.host.view_serial_number
            with _ ->
                null

        if not (request_is_valid request view) then
            cancel request.id
    | None -> ()

let queue
    (host: ViewportHostIdentity)
    (mode: FlightMode)
    (held: (unit -> bool) option)
    (held_buttons: uint32)
    (entry_press: MouseEntryPress option)
    (deadline: int64)
    (is_current: unit -> bool)
    =
    revoke ()
    let id = Guid.NewGuid()

    let request =
        { id = id
          host = host
          mode = mode
          held = held
          held_buttons = held_buttons
          entry_press = entry_press
          deadline = deadline
          generation = generation
          is_current = is_current
          invalidated = ref false
          focus_watch = None }

    pending <- Some request

    try
        request.focus_watch <- PlatformInput.watch_entry_focus host request.invalidated

        if request.generation <> generation then
            release_watch request
    with _ ->
        cancel id
        reraise ()

    id

let consume (view: RhinoView) (id: Guid) =
    match pending with
    | Some request when request.id = id ->
        pending <- None
        release_watch request

        if request_is_valid request view then
            Some
                { mode = request.mode
                  held = request.held
                  held_buttons = request.held_buttons
                  entry_press = request.entry_press
                  valid = fun () -> request_is_valid request view }
        else
            None
    | _ -> None
