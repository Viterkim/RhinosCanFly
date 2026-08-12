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

    snapshot ()
    |> Array.map (fun (entry: Entry) ->
        let seconds = float entry.timestamp / frequency

        $"{entry.sequence, 5} {seconds, 12:F6} managed={entry.managed_thread} native={entry.native_thread} {entry.kind} {entry.value1} {entry.value2}")
