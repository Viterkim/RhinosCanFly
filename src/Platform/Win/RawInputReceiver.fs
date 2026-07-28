namespace RhinosCanFly.Platform.Win

open System
open System.Diagnostics
open System.Runtime.InteropServices
open System.Windows.Forms
open RhinosCanFly

type RawInputReceiver(config: FlyConfig, input: InputAccumulator.State, inputAvailable: Action) as self =
    inherit NativeWindow()

    let bufferCapacity = 128
    let buffer = Marshal.AllocHGlobal bufferCapacity
    let mutable handleCreated = false
    let mutable bufferFreed = false
    let mutable rawRegistered = false
    let mutable registrationRestored = false
    let mutable previousMouse: RawInputNative.Device option = None

    let restore_registration () =
        if rawRegistered && not registrationRestored then
            registrationRestored <- true

            match RawInputNative.restore_mouse previousMouse with
            | Ok() -> ()
            | Error restoreError ->
                Debug.WriteLine $"RhinosCanFly: {restoreError}"

                match previousMouse with
                | Some _ ->
                    match RawInputNative.unregister_mouse () with
                    | Ok() -> ()
                    | Error removalError -> Debug.WriteLine $"RhinosCanFly: {removalError}"
                | None -> ()

    let release_resources () =
        restore_registration ()

        if handleCreated then
            self.DestroyHandle()
            handleCreated <- false

        if not bufferFreed then
            Marshal.FreeHGlobal buffer
            bufferFreed <- true

    let process_mouse (mouse: RawInputNative.Mouse) =
        if mouse.flags &&& RawInputNative.mouse_move_absolute = 0us then
            InputAccumulator.add_mouse mouse.last_x mouse.last_y input

        let flags = RawInputNative.button_flags mouse

        if flags &&& RawInputNative.mouse_wheel <> 0us then
            InputAccumulator.add_wheel (RawInputNative.signed_button_data mouse) input

        if
            config.exit_on_mouse_left && flags &&& RawInputNative.left_button_down <> 0us
            || config.exit_on_mouse_right && flags &&& RawInputNative.right_button_down <> 0us
            || config.exit_on_mouse_middle
               && flags &&& RawInputNative.middle_button_down <> 0us
        then
            InputAccumulator.request_exit input

        inputAvailable.Invoke()

    do
        try
            let parameters = CreateParams()
            parameters.Caption <- "RhinosCanFly raw input"
            parameters.Parent <- nativeint RawInputNative.message_only_window
            self.CreateHandle parameters
            handleCreated <- true

            match RawInputNative.get_registered_mouse () with
            | Error error -> failwith error
            | Ok previous ->
                previousMouse <- previous

                match RawInputNative.register_mouse self.Handle with
                | Ok() -> rawRegistered <- true
                | Error error -> failwith error
        with _ ->
            release_resources ()
            reraise ()

    member _.WindowHandle = self.Handle

    member _.ReleaseResources() = release_resources ()

    override _.WndProc(message: byref<Message>) =
        if message.Msg = RawInputNative.stop_message then
            message.Result <- nativeint 0
            Application.ExitThread()
        elif message.Msg = RawInputNative.message then
            let mutable mouse = Unchecked.defaultof<RawInputNative.Mouse>

            if RawInputNative.try_read_mouse message.LParam buffer bufferCapacity &mouse then
                process_mouse mouse

            base.WndProc(&message)
        else
            base.WndProc(&message)
