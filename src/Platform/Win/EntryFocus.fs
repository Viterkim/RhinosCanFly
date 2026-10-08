module RhinosCanFly.Platform.Win.EntryFocus

open System
open System.Windows.Forms

type Watcher(window: nativeint, invalidated: bool ref) as self =
    inherit NativeWindow()

    do self.AssignHandle window

    override _.WndProc(message: byref<Message>) =
        if
            (message.Msg = Win32Native.WM_ACTIVATEAPP && message.WParam = 0n)
            || (message.Msg = Win32Native.WM_ACTIVATE && int64 message.WParam &&& 0xffffL = 0L)
            || message.Msg = Win32Native.WM_NCDESTROY
        then
            invalidated.Value <- true

        base.WndProc(&message)

    interface IDisposable with
        member _.Dispose() = self.ReleaseHandle()
