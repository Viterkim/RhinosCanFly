namespace RhinosCanFly

open System
open System.Diagnostics
open System.Runtime.InteropServices
open System.Windows.Forms

type RawInputWindow(handle: nativeint, state: FlyState) as self =
    inherit NativeWindow()
    let buffer = Marshal.AllocHGlobal 128
    let mutable disposed = false
    let mutable handleAssigned = false
    let mutable bufferFreed = false
    let mutable previousRawMouse: Win32.RawInputDevice option = None

    let release_resources () =
        if handleAssigned then
            self.ReleaseHandle()
            handleAssigned <- false

        if not bufferFreed then
            Marshal.FreeHGlobal buffer
            bufferFreed <- true

    do
        try
            self.AssignHandle handle
            handleAssigned <- true

            match Win32.get_registered_raw_mouse () with
            | Error error -> failwith error
            | Ok previous ->
                match Win32.register_raw_mouse handle with
                | Ok() -> previousRawMouse <- previous
                | Error error -> failwith error
        with _ ->
            release_resources ()
            reraise ()

    override _.WndProc(message: byref<Message>) =
        if message.Msg = Win32.WM_INPUT then
            match Win32.try_read_raw_mouse message.LParam buffer 128 with
            | Some mouse ->
                if mouse.flags &&& Win32.MOUSE_MOVE_ABSOLUTE = 0us then
                    state.mouse_dx <- state.mouse_dx + int64 mouse.last_x
                    state.mouse_dy <- state.mouse_dy + int64 mouse.last_y

                let flags = Win32.raw_button_flags mouse

                if flags &&& Win32.RI_MOUSE_WHEEL <> 0us then
                    state.wheel_delta <- state.wheel_delta + Win32.signed_button_data mouse

                if
                    state.config.exit_on_mouse_left
                    && flags &&& Win32.RI_MOUSE_LEFT_BUTTON_DOWN <> 0us
                    || state.config.exit_on_mouse_right
                       && flags &&& Win32.RI_MOUSE_RIGHT_BUTTON_DOWN <> 0us
                    || state.config.exit_on_mouse_middle
                       && flags &&& Win32.RI_MOUSE_MIDDLE_BUTTON_DOWN <> 0us
                then
                    state.running <- false
            | None -> ()

            base.WndProc(&message)
        elif
            [ Win32.WM_KEYDOWN
              Win32.WM_KEYUP
              Win32.WM_CHAR
              Win32.WM_SYSKEYDOWN
              Win32.WM_SYSKEYUP
              Win32.WM_SYSCHAR ]
            |> List.contains message.Msg
        then
            message.Result <- nativeint 0
        else
            base.WndProc(&message)

    interface IDisposable with
        member _.Dispose() =
            if not disposed then
                disposed <- true

                try
                    match Win32.restore_raw_mouse previousRawMouse with
                    | Ok() -> ()
                    | Error restoreError ->
                        Debug.WriteLine $"RhinosCanFly: {restoreError}"

                        match previousRawMouse with
                        | Some _ ->
                            match Win32.unregister_raw_mouse () with
                            | Ok() -> ()
                            | Error removalError -> Debug.WriteLine $"RhinosCanFly: {removalError}"
                        | None -> ()
                finally
                    release_resources ()
