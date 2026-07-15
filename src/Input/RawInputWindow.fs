namespace RhinosCanFly

open System
open System.ComponentModel
open System.Runtime.InteropServices
open System.Windows.Forms

type RawInputWindow(handle: nativeint, state: FlyState) as self =
    inherit NativeWindow()
    let buffer = Marshal.AllocHGlobal 128
    let mutable disposed = false

    do
        self.AssignHandle handle

        if not (Win32.register_raw_mouse handle) then
            self.ReleaseHandle()
            Marshal.FreeHGlobal buffer
            raise (Win32Exception(Marshal.GetLastWin32Error()))

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
                Win32.unregister_raw_mouse () |> ignore
                self.ReleaseHandle()
                Marshal.FreeHGlobal buffer
