module RhinosCanFly.Platform.Linux.WaylandInput

open System
open System.IO
open System.Runtime.InteropServices
open RhinosCanFly

// Borrowed from the host's registry and seat, all on the same event queue.
type Host =
    { relative_pointer_manager: nativeint
      pointer_constraints: nativeint
      pointer: nativeint
      surface: nativeint
      event_queue: nativeint
      set_cursor_hidden: Action<bool> }

type State =
    | WaitingForLock
    | Locked
    | Released
    | Failed of string
    | Stopped

[<UnmanagedFunctionPointer(CallingConvention.Cdecl)>]
type StateHandler = delegate of uint32 -> unit

[<UnmanagedFunctionPointer(CallingConvention.Cdecl)>]
type Begin =
    delegate of
        nativeint * nativeint * nativeint * nativeint * nativeint * RelativeMotion.Handler * StateHandler -> nativeint

[<UnmanagedFunctionPointer(CallingConvention.Cdecl)>]
type End = delegate of nativeint -> unit

[<UnmanagedFunctionPointer(CallingConvention.Cdecl)>]
type Size = delegate of unit -> uint32

type Api =
    { begin_session: Begin
      end_session: End }

let mutable api: Api option = None

let export<'t when 't :> Delegate> (library: nativeint) (name: string) =
    Marshal.GetDelegateForFunctionPointer<'t>(NativeLibrary.GetExport(library, name))

let load () =
    match api with
    | Some loaded -> loaded
    | None ->
        if not (OperatingSystem.IsLinux()) then
            invalidOp "Wayland input requires Linux."

        let directory = Path.GetDirectoryName typeof<Host>.Assembly.Location

        let library =
            NativeLibrary.Load(Path.Combine(directory, "libRhinosCanFlyWayland.so"))

        try
            if
                (export<Size> library "rcf_wayland_abi").Invoke() <> 1u
                || (export<Size> library "rcf_wayland_motion_size").Invoke()
                   <> uint32 (Marshal.SizeOf<RelativeMotion.Packet>())
            then
                invalidOp "The Wayland input bridge does not match this build."

            let loaded =
                { begin_session = export<Begin> library "rcf_wayland_begin"
                  end_session = export<End> library "rcf_wayland_end" }

            api <- Some loaded
            loaded
        with _ ->
            NativeLibrary.Free library
            reraise ()

// Create, inspect and dispose on the host's Wayland dispatch thread.
// Callbacks only enqueue input. They must not dispose or pump Wayland recursively.
type Session(host: Host, publish: Action<int64, int64, int64>) as this =
    let owner_thread = Environment.CurrentManagedThreadId
    let native = load ()
    let decoder = RelativeMotion.Decoder ValueNone
    let mutable state = WaitingForLock
    let mutable pointer = 0n
    let mutable root = Unchecked.defaultof<GCHandle>
    let mutable cursor_hidden = false

    let motion =
        RelativeMotion.Handler(fun (packet: byref<RelativeMotion.Packet>) ->
            if state = Locked then
                try
                    let movement = decoder.Decode packet

                    if movement.dx <> 0L || movement.dy <> 0L then
                        publish.Invoke(movement.timestamp, movement.dx, movement.dy)
                with error ->
                    state <- Failed error.Message)

    let state_changed =
        StateHandler(fun (value: uint32) ->
            if state = WaitingForLock || state = Locked then
                try
                    if value = 1u then
                        cursor_hidden <- true
                        host.set_cursor_hidden.Invoke true
                        state <- Locked
                    else
                        state <- Released

                        if cursor_hidden then
                            host.set_cursor_hidden.Invoke false
                            cursor_hidden <- false
                with error ->
                    state <- Failed error.Message)

    do
        if isNull publish then
            nullArg (nameof publish)

        if isNull host.set_cursor_hidden then
            invalidArg (nameof host) "The host must supply cursor hiding and restoration."

        if
            host.relative_pointer_manager = 0n
            || host.pointer_constraints = 0n
            || host.pointer = 0n
            || host.surface = 0n
        then
            invalidArg
                (nameof host)
                "Wayland relative-pointer and pointer-constraints globals and a focused surface are required."

        pointer <-
            native.begin_session.Invoke(
                host.relative_pointer_manager,
                host.pointer_constraints,
                host.pointer,
                host.surface,
                host.event_queue,
                motion,
                state_changed
            )

        if pointer = 0n then
            invalidOp "Wayland could not create a relative pointer lock."

        try
            root <- GCHandle.Alloc this
        with _ ->
            native.end_session.Invoke pointer
            pointer <- 0n
            reraise ()

    member _.State =
        if Environment.CurrentManagedThreadId <> owner_thread then
            invalidOp "Read Wayland session state on its dispatch thread."

        state

    interface IDisposable with
        member _.Dispose() =
            if Environment.CurrentManagedThreadId <> owner_thread then
                invalidOp "Release Wayland input on its dispatch thread."

            if pointer <> 0n then
                state <- Stopped
                native.end_session.Invoke pointer
                pointer <- 0n
                GC.KeepAlive motion
                GC.KeepAlive state_changed

                if root.IsAllocated then
                    root.Free()

            if cursor_hidden then
                host.set_cursor_hidden.Invoke false
                cursor_hidden <- false
