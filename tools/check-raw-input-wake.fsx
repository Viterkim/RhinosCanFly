open System
open System.IO
open System.Threading

type State =
    { mutable pending: int
      mutable messages: int
      mutable work: int
      mutable revision: int64 }

let produce (state: State) =
    Interlocked.Increment(&state.work) |> ignore
    Interlocked.Increment(&state.revision) |> ignore

    if Interlocked.CompareExchange(&state.pending, 1, 0) = 0 then
        Interlocked.Increment(&state.messages) |> ignore

let acknowledge (state: State) =
    Interlocked.Exchange(&state.pending, 0) |> ignore

let reset (state: State) =
    Interlocked.Exchange(&state.pending, 0) |> ignore
    Interlocked.Exchange(&state.messages, 0) |> ignore
    Interlocked.Exchange(&state.work, 0) |> ignore

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

let sessionSource =
    File.ReadAllText(Path.Combine(sourceRoot, "src", "Fly", "FlightSession.fs"))

if not (wakeSource.Contains("PostMessage", StringComparison.Ordinal)) then
    fail "RawInputWake must wake Rhino through its native message loop."

if wakeSource.Contains("AutoResetEvent", StringComparison.Ordinal) then
    fail "RawInputWake must not own a private wait handle."

if
    not (loopSource.Contains("PlatformInput.wait_for_input", StringComparison.Ordinal))
    || not (loopSource.Contains("RhinoApp.Wait", StringComparison.Ordinal))
then
    fail "FlightLoop must sleep while idle and pump Rhino before applying the next frame."

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

let startingIndex =
    sessionSource.IndexOf("let process_starting", StringComparison.Ordinal)

if startingIndex < 0 then
    fail "FlightSession.process_starting was not found."

let startingAcknowledgeIndex =
    sessionSource.IndexOf("PlatformInput.acknowledge_raw_input_wake", startingIndex, StringComparison.Ordinal)

let startingWakeIndex =
    sessionSource.IndexOf("PlatformInput.wake_flight_loop", startingIndex, StringComparison.Ordinal)

if
    startingAcknowledgeIndex < startingIndex
    || startingWakeIndex < startingAcknowledgeIndex
then
    fail "The viewport-capture wait must acknowledge each coalesced wake before scheduling another check."

let model =
    { pending = 0
      messages = 0
      work = 0
      revision = 0L }

produce model
produce model

if model.messages <> 1 then
    fail "Coalesced work posted more than one wake message."

let observed = Volatile.Read(&model.revision)
Interlocked.Exchange(&model.work, 0) |> ignore

// Work arriving before acknowledgement cannot post another message, so the
// revision check must keep the consumer running.
produce model
acknowledge model

if not (work_pending_since observed model) then
    fail "Work arriving before acknowledgement was lost."

reset model
let observedAfterDrain = Volatile.Read(&model.revision)
acknowledge model
produce model

if not (work_pending_since observedAfterDrain model) || model.messages <> 1 then
    fail "Work arriving after acknowledgement was not observed and posted."

printfn "RawInputWake ordering check passed."
