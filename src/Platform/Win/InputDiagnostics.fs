module RhinosCanFly.Platform.Win.InputDiagnostics

open System
open System.Diagnostics
open System.Threading

type EventKind =
    | RawStart = 1
    | RawReady = 2
    | RawStartFailed = 3
    | RawStopRequested = 4
    | RawStopped = 5
    | RawStopTimedOut = 6
    | RegistrationAcquired = 7
    | RegistrationReleased = 8
    | RegistrationReplaced = 9
    | HookInstalled = 10
    | HookRemoved = 11
    | HookRemovalFailed = 12
    | TimerFast = 13
    | TimerWatchdog = 14
    | TimerStopped = 15
    | SyntheticMiddleDown = 16
    | SyntheticMiddleUp = 17
    | SyntheticShiftDown = 18
    | SyntheticShiftUp = 19
    | InputSuspended = 20
    | InputResumed = 21
    | ViewCaptureReleased = 22
    | ViewCaptureTimedOut = 23
    | NavigationTransitionTimedOut = 24
    | CursorVisibilityChanged = 25

[<Struct>]
type Entry =
    { sequence: int64
      timestamp: int64
      managed_thread: int
      native_thread: uint32
      kind: EventKind
      value1: int64
      value2: int64 }

[<Literal>]
let capacity = 512

let entries = Array.zeroCreate<Entry> capacity
let published = Array.zeroCreate<int64> capacity
let mutable sequence = 0L
let mutable rawPackets = 0L
let mutable wakeSignals = 0L
let mutable loopIterations = 0L
let mutable cameraApplications = 0L
let mutable redraws = 0L
let startedAt = Stopwatch.GetTimestamp()
let mutable latestRawTimestamp = 0L
let mutable lastInputToCameraTicks = 0L
let mutable maximumInputToCameraTicks = 0L
let mutable lastRedrawTicks = 0L
let mutable maximumRedrawTicks = 0L
let mutable lastCameraSetterTicks = 0L
let mutable maximumCameraSetterTicks = 0L
let mutable lastRhinoWaitTicks = 0L
let mutable maximumRhinoWaitTicks = 0L
let mutable maximumMouseCounts = 0L

let update_maximum (location: byref<int64>) (value: int64) =
    let mutable current = Volatile.Read(&location)

    while value > current
          && Interlocked.CompareExchange(&location, value, current) <> current do
        current <- Volatile.Read(&location)

let record_raw_packet () =
    Interlocked.Increment(&rawPackets) |> ignore
    Volatile.Write(&latestRawTimestamp, Stopwatch.GetTimestamp())

let record_wake_signal () =
    Interlocked.Increment(&wakeSignals) |> ignore

let record_loop_iteration () =
    Interlocked.Increment(&loopIterations) |> ignore

let record_camera_application (setterTicks: int64) =
    Interlocked.Increment(&cameraApplications) |> ignore
    Volatile.Write(&lastCameraSetterTicks, setterTicks)
    update_maximum &maximumCameraSetterTicks setterTicks
    let rawTimestamp = Volatile.Read(&latestRawTimestamp)

    if rawTimestamp <> 0L then
        let elapsed = max 0L (Stopwatch.GetTimestamp() - rawTimestamp)
        Volatile.Write(&lastInputToCameraTicks, elapsed)
        update_maximum &maximumInputToCameraTicks elapsed

let record_redraw (elapsedTicks: int64) =
    Interlocked.Increment(&redraws) |> ignore
    Volatile.Write(&lastRedrawTicks, elapsedTicks)
    update_maximum &maximumRedrawTicks elapsedTicks

let record_rhino_wait (elapsedTicks: int64) =
    Volatile.Write(&lastRhinoWaitTicks, elapsedTicks)
    update_maximum &maximumRhinoWaitTicks elapsedTicks

let record_mouse_drain (dx: int64) (dy: int64) =
    update_maximum &maximumMouseCounts (max (abs dx) (abs dy))

let record (kind: EventKind) (value1: int64) (value2: int64) =
    let next = Interlocked.Increment(&sequence)
    let index = int ((next - 1L) % int64 capacity)
    Volatile.Write(&published[index], 0L)

    entries[index] <-
        { sequence = next
          timestamp = Stopwatch.GetTimestamp()
          managed_thread = Thread.CurrentThread.ManagedThreadId
          native_thread = Win32Native.GetCurrentThreadId()
          kind = kind
          value1 = value1
          value2 = value2 }

    Volatile.Write(&published[index], next)

let snapshot () =
    let newest = Volatile.Read(&sequence)

    if newest = 0L then
        Array.empty
    else
        let oldest = max 1L (newest - int64 capacity + 1L)
        let result = ResizeArray<Entry>(int (newest - oldest + 1L))

        for expected in oldest..newest do
            let index = int ((expected - 1L) % int64 capacity)
            let before = Volatile.Read(&published[index])
            let entry = entries[index]
            let after = Volatile.Read(&published[index])

            if before = expected && after = expected && entry.sequence = expected then
                result.Add entry

        result.ToArray()

let lines () =
    let frequency = float Stopwatch.Frequency

    let milliseconds (ticks: int64) = float ticks * 1000. / frequency

    let elapsedSeconds =
        max 0.001 (float (Stopwatch.GetTimestamp() - startedAt) / frequency)

    let counters =
        [| $"Counters: raw packets={Volatile.Read(&rawPackets)} ({float (Volatile.Read(&rawPackets)) / elapsedSeconds:F1}/s); wake signals={Volatile.Read(&wakeSignals)} ({float (Volatile.Read(&wakeSignals)) / elapsedSeconds:F1}/s); loop iterations={Volatile.Read(&loopIterations)}; camera applications={Volatile.Read(&cameraApplications)}; redraws={Volatile.Read(&redraws)}"
           $"Timing: input to camera last={milliseconds (Volatile.Read(&lastInputToCameraTicks)):F3} ms; max={milliseconds (Volatile.Read(&maximumInputToCameraTicks)):F3} ms; camera setter last={milliseconds (Volatile.Read(&lastCameraSetterTicks)):F3} ms; max={milliseconds (Volatile.Read(&maximumCameraSetterTicks)):F3} ms"
           $"Timing: RhinoApp.Wait last={milliseconds (Volatile.Read(&lastRhinoWaitTicks)):F3} ms; max={milliseconds (Volatile.Read(&maximumRhinoWaitTicks)):F3} ms; redraw last={milliseconds (Volatile.Read(&lastRedrawTicks)):F3} ms; max={milliseconds (Volatile.Read(&maximumRedrawTicks)):F3} ms; max drained axis counts={Volatile.Read(&maximumMouseCounts)}" |]

    Array.append
        counters
        (snapshot ()
         |> Array.map (fun (entry: Entry) ->
             let seconds = float entry.timestamp / frequency

             $"{entry.sequence, 5} {seconds, 12:F6} managed={entry.managed_thread} native={entry.native_thread} {entry.kind} {entry.value1} {entry.value2}"))
