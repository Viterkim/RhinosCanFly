module RhinosCanFly.InputDebugTrace

open System
open System.Diagnostics
open System.IO
open System.Text
open System.Threading

let path = Path.Combine(Path.GetTempPath(), "RhinosCanFly-input-debug.log")
let gate = obj ()
let mutable stream: FileStream option = None
let mutable writer: StreamWriter option = None

let close_writer () =
    writer |> Option.iter (fun (value: StreamWriter) -> value.Dispose())
    writer <- None
    stream <- None

let write_unlocked (message: string) =
    match writer with
    | Some value ->
        value.WriteLine(
            $"{DateTimeOffset.Now:O} ticks={Stopwatch.GetTimestamp()} thread={Thread.CurrentThread.ManagedThreadId} {message}"
        )

        value.Flush()
    | None -> ()

let start_session () =
    lock gate (fun () ->
        try
            close_writer ()

            let output =
                new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite)

            let text = new StreamWriter(output, new UTF8Encoding(false))
            text.AutoFlush <- true
            stream <- Some output
            writer <- Some text

            use currentProcess = Process.GetCurrentProcess()
            write_unlocked $"SESSION START process={currentProcess.Id} stopwatch-frequency={Stopwatch.Frequency}"
        with _ ->
            close_writer ())

let write (message: string) =
    lock gate (fun () ->
        try
            write_unlocked message
        with _ ->
            ())

let stop_session () =
    lock gate (fun () ->
        try
            write_unlocked "SESSION END"
            close_writer ()
        with _ ->
            close_writer ())
