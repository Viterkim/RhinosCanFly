module RhinosCanFly.PlatformLifecycle

let prepare () = PlatformRawInput.prepare ()

let shutdown (report: string -> unit) =
    try
        PlatformMouseActions.shutdown ()
    with error ->
        report $"RhinosCanFly mouse override shutdown failed: {error.Message}"

    try
        match PlatformFlightKeyboard.shutdown () with
        | Ok() -> ()
        | Error error -> report $"RhinosCanFly keyboard hook shutdown failed: {error}"
    with error ->
        report $"RhinosCanFly keyboard hook shutdown failed: {error.Message}"

    try
        let struct (remaining, errors) = PlatformRawInput.retry_recovery ()

        for error in errors do
            report $"RhinosCanFly raw-input recovery: {error}"

        if remaining > 0 then
            report $"RhinosCanFly raw-input recovery still owns {remaining} cleanup item(s)."
    with error ->
        report $"RhinosCanFly raw-input recovery failed: {error.Message}"

    try
        for error in PlatformRawInput.shutdown () do
            report $"RhinosCanFly raw-input worker shutdown: {error}"
    with error ->
        report $"RhinosCanFly raw-input worker shutdown failed: {error.Message}"

    try
        let struct (remaining, errors) = PlatformCursorClip.retry_cleanup ()

        for error in errors do
            report $"RhinosCanFly cursor-clip recovery: {error}"

        if remaining > 0 then
            report $"RhinosCanFly cursor-clip recovery still owns {remaining} cleanup item(s)."
    with error ->
        report $"RhinosCanFly cursor-clip recovery failed: {error.Message}"
