module RhinosCanFly.PlatformCursorClip

open System
open Rhino.Display
open RhinosCanFly.Platform.Win

let owned = ResizeArray<CursorClipLease>()

let retain (lease: CursorClipLease) =
    let exists =
        owned
        |> Seq.exists (fun (candidate: CursorClipLease) -> Object.ReferenceEquals(candidate, lease))

    if not exists then
        owned.Add lease

let forget (lease: CursorClipLease) =
    let mutable index = owned.Count - 1

    while index >= 0 do
        if Object.ReferenceEquals(owned[index], lease) then
            owned.RemoveAt index

        index <- index - 1

let rec acquire (view: RhinoView) =
    let rectangle = view.ScreenRectangle

    match Win32.get_cursor_clip () with
    | Error error -> Error error
    | Ok previous ->
        match Win32.clip_cursor rectangle with
        | Error error -> Error error
        | Ok() ->
            let lease =
                { previous = previous
                  installed = rectangle
                  relinquished = false }

            retain lease
            let verification = Win32.get_cursor_clip ()

            match verification with
            | Ok current when current = rectangle -> Ok lease
            | _ ->
                let verificationError =
                    match verification with
                    | Ok _ -> "The cursor clip changed before it could be verified."
                    | Error error -> $"The cursor clip could not be verified: {error}"

                match release lease with
                | Ok() -> Error verificationError
                | Error releaseError -> Error $"{verificationError}; cleanup failed: {releaseError}"

and release (lease: CursorClipLease) =
    if lease.relinquished then
        forget lease
        Ok()
    else
        match Win32.get_cursor_clip () with
        | Error error ->
            retain lease
            Error error
        | Ok current when current <> lease.installed ->
            lease.relinquished <- true
            forget lease
            Ok()
        | Ok _ ->
            match Win32.clip_cursor lease.previous with
            | Error error ->
                retain lease
                Error error
            | Ok() ->
                match Win32.get_cursor_clip () with
                | Error error ->
                    retain lease
                    Error $"The previous cursor clip was restored but could not be verified: {error}"
                | Ok current when current = lease.previous || current <> lease.installed ->
                    lease.relinquished <- true
                    forget lease
                    Ok()
                | Ok _ ->
                    retain lease
                    Error "The cursor clip still belongs to RhinosCanFly after restoration."

let retry_cleanup () =
    let pending = owned.ToArray()
    let errors = ResizeArray<string>()

    for lease in pending do
        match release lease with
        | Ok() -> ()
        | Error error -> errors.Add error

    struct (owned.Count, List.ofSeq errors)

let recovery_count () = owned.Count
