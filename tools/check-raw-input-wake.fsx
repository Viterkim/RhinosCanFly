open System
open System.IO
open System.Threading

type State =
    { signal: AutoResetEvent
      mutable pending: int
      mutable work: int
      mutable revision: int64 }

let produce (state: State) =
    Interlocked.Increment(&state.work) |> ignore
    Interlocked.Increment(&state.revision) |> ignore

    if Interlocked.CompareExchange(&state.pending, 1, 0) = 0 then
        state.signal.Set() |> ignore

let acknowledge (state: State) =
    Interlocked.Exchange(&state.pending, 0) |> ignore

let reset (state: State) =
    Interlocked.Exchange(&state.pending, 0) |> ignore
    Interlocked.Exchange(&state.work, 0) |> ignore

    while state.signal.WaitOne(0) do
        ()

let work_pending_since (observed: int64) (state: State) =
    Volatile.Read(&state.revision) <> observed

let fail (message: string) =
    Console.Error.WriteLine message
    Environment.Exit 1

let sourceRoot = Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, ".."))

let wakeSource =
    File.ReadAllText(Path.Combine(sourceRoot, "src", "Platform", "Win", "RawInputWake.fs"))

let loopSource =
    File.ReadAllText(Path.Combine(sourceRoot, "src", "Fly", "FlightLoop.fs"))

if wakeSource.Contains("WaitOne(0)", StringComparison.Ordinal) then
    fail "RawInputWake must not consume the event while acknowledging pending work."

let revisionIndex =
    loopSource.IndexOf("InputAccumulator.work_revision", StringComparison.Ordinal)

let stateDrainIndex =
    loopSource.IndexOf("FlightControls.update_state", StringComparison.Ordinal)

let discardIndex =
    loopSource.IndexOf("InputAccumulator.discard_transient_input", StringComparison.Ordinal)

let mouseDrainIndex =
    loopSource.IndexOf("FlightCamera.apply_mouse_input", StringComparison.Ordinal)

let acknowledgeIndex =
    loopSource.IndexOf("PlatformInput.acknowledge_raw_input_wake", StringComparison.Ordinal)

let recheckIndex =
    loopSource.IndexOf("InputAccumulator.work_pending_since", StringComparison.Ordinal)

if
    revisionIndex < 0
    || stateDrainIndex < revisionIndex
    || discardIndex < stateDrainIndex
    || mouseDrainIndex < discardIndex
    || acknowledgeIndex < mouseDrainIndex
    || recheckIndex < acknowledgeIndex
then
    fail "FlightLoop must observe work, drain it, acknowledge the wake, then recheck work."

let signal = new AutoResetEvent(false)

let model =
    { signal = signal
      pending = 0
      work = 0
      revision = 0L }

for _iteration in 1..100_000 do
    reset model
    produce model
    model.signal.WaitOne(0) |> ignore

    let observed = Volatile.Read(&model.revision)
    Interlocked.Exchange(&model.work, 0) |> ignore

    // Producer arrives after the drain but before acknowledgement. It cannot
    // signal because pending is still one, so only the revision recheck saves it.
    produce model
    acknowledge model

    if not (work_pending_since observed model) then
        fail "Work arriving before acknowledgement would wait for the watchdog."

    Interlocked.Exchange(&model.work, 0) |> ignore
    let observedAfterDrain = Volatile.Read(&model.revision)
    acknowledge model

    // Producer arrives after acknowledgement but before the revision recheck.
    // The consumer continues immediately and leaves one harmless stale event.
    produce model

    if not (work_pending_since observedAfterDrain model) then
        fail "Work arriving after acknowledgement would wait for the watchdog."

    if not (model.signal.WaitOne(0)) then
        fail "Work arriving after acknowledgement did not leave a wake signal."

    reset model
    let observedBeforeWait = Volatile.Read(&model.revision)
    acknowledge model

    if work_pending_since observedBeforeWait model then
        fail "The idle model unexpectedly reported work."

    // Producer arrives after the final recheck. The event must make the next
    // wait immediate instead of relying on the 75 ms watchdog.
    produce model

    if not (model.signal.WaitOne(0)) then
        fail "Work arriving before sleep would wait for the watchdog."

signal.Dispose()
printfn "RawInputWake interleaving check passed (100000 iterations)."
