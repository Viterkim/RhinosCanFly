module RhinosCanFly.BindingCapture

open System
open System.Diagnostics
open Eto.Drawing
open Eto.Forms

type Active = { field: TextBox; button: Button }

type State =
    { focus_sink: Drawable
      side_button_timer: UITimer
      mutable active: Active option
      mutable modifier_keys: Keys list
      mutable suppress_next_set_click: Button option
      mutable disposed: bool }

[<Literal>]
let SIDE_BUTTON_POLL_INTERVAL_SECONDS = 0.015

let stop (state: State) =
    match state.active with
    | Some active -> active.button.Text <- "Set..."
    | None -> ()

    state.active <- None
    state.modifier_keys <- []

    if not state.disposed && state.side_button_timer.Started then
        state.side_button_timer.Stop()

let cancel (state: State) =
    stop state
    state.suppress_next_set_click <- None

let complete (state: State) (binding: string) =
    match state.active with
    | Some active ->
        if String.IsNullOrWhiteSpace binding then
            invalidOp "No usable input was captured."

        match PlatformBindings.parse binding with
        | Error error -> invalidOp error
        | Ok parsed -> PlatformBindings.execution_error parsed |> Option.iter invalidOp

        active.field.Text <- binding
        stop state
    | None -> ()

let start (state: State) (field: TextBox) (button: Button) =
    if not state.disposed then
        stop state
        state.active <- Some { field = field; button = button }
        button.Text <- "Press..."
        button.ToolTip <- ""
        button.Focus()
        state.side_button_timer.Start()

let record_modifiers (state: State) (key: Keys) (modifiers: Keys) =
    let group = PlatformBindings.capture_names.modifier_group

    for modifier in PlatformBindings.capture_names.modifiers do
        if
            modifiers &&& modifier = modifier
            && not (
                state.modifier_keys
                |> List.exists (fun (recorded: Keys) -> group recorded = modifier)
            )
        then
            state.modifier_keys <- state.modifier_keys @ [ modifier ]

    let key = BindingNames.key_value key
    let modifier = group key

    if key <> modifier && List.contains modifier state.modifier_keys then
        state.modifier_keys <-
            state.modifier_keys
            |> List.map (fun (recorded: Keys) -> if recorded = modifier then key else recorded)
    elif
        not (
            state.modifier_keys
            |> List.exists (fun (recorded: Keys) -> recorded = key || (key = modifier && group recorded = modifier))
        )
    then
        state.modifier_keys <- state.modifier_keys @ [ key ]

let key_down (state: State) (key: Keys) (modifiers: Keys) =
    let key = PlatformBindings.capture_key_value key

    if BindingNames.is_modifier_key PlatformBindings.capture_names key then
        record_modifiers state key modifiers

        None
    else
        Some(BindingNames.binding_from_key PlatformBindings.capture_names key modifiers)

let key_up (state: State) (key: Keys) (modifiers: Keys) =
    let key = PlatformBindings.capture_key_value key

    if BindingNames.is_modifier_key PlatformBindings.capture_names key then
        record_modifiers state key modifiers

        state.modifier_keys
        |> List.map PlatformBindings.capture_names.key_name
        |> String.concat "+"
        |> Some
    else
        None

let capture_key (state: State) (read_binding: unit -> string option) =
    let result =
        try
            read_binding () |> Option.iter (complete state)
            Ok()
        with error ->
            Error error.Message

    match result with
    | Ok() -> ()
    | Error error ->
        let button = state.active |> Option.map (fun (active: Active) -> active.button)
        cancel state

        button
        |> Option.iter (fun (button: Button) -> button.ToolTip <- $"Binding capture failed: {error}")

        SettingsUi.report_error $"Binding capture failed: {error}"

let editor (state: State) (field: TextBox) (default_value: string) =
    let update_support () =
        field.ToolTip <-
            if String.IsNullOrWhiteSpace field.Text then
                ""
            else
                match PlatformBindings.parse field.Text with
                | Ok binding -> PlatformBindings.execution_error binding |> Option.defaultValue ""
                | Error error -> error

    update_support ()
    field.TextChanged.Add(fun (_: EventArgs) -> update_support ())
    let set_button = new Button(Text = "Set...", Width = 62, Height = 24)
    let default_button = new Button(Text = "Default", Width = 66, Height = 24)
    let panel = new TableLayout(Spacing = Size(6, 0))

    panel.Rows.Add(
        SettingsLayout.row
            [ new TableCell(field, true)
              new TableCell(set_button, false)
              new TableCell(default_button, false) ]
    )

    set_button.Click.Add(fun (_: EventArgs) ->
        match state.suppress_next_set_click with
        | Some suppressed when Object.ReferenceEquals(suppressed, set_button) -> state.suppress_next_set_click <- None
        | Some _ ->
            state.suppress_next_set_click <- None
            start state field set_button
        | None -> start state field set_button)

    set_button.KeyDown.Add(fun (event: KeyEventArgs) ->
        match state.active with
        | Some active when Object.ReferenceEquals(active.button, set_button) ->
            event.Handled <- true

            capture_key state (fun () -> key_down state event.Key event.Modifiers)
        | _ -> ())

    set_button.KeyUp.Add(fun (event: KeyEventArgs) ->
        match state.active with
        | Some active when Object.ReferenceEquals(active.button, set_button) ->
            event.Handled <- true

            capture_key state (fun () -> key_up state event.Key event.Modifiers)
        | _ -> ())

    default_button.Click.Add(fun (_: EventArgs) -> field.Text <- default_value)
    panel :> Control

let is_editor_control (control: Control) =
    match control with
    | :? TextBox
    | :? TextArea
    | :? Button
    | :? CheckBox
    | :? NumericStepper
    | :? DropDown -> true
    | _ -> false

let try_capture_mouse (state: State) (source: Control) (event: MouseEventArgs) =
    match state.active with
    | Some _ ->
        let mutable primary = event.Buttons = MouseButtons.Primary
        let mutable captured = false

        capture_key state (fun () ->
            let struct (binding, physical_primary) =
                PlatformBindings.capture_mouse event.Buttons event.Modifiers

            primary <- physical_primary
            captured <- Option.isSome binding
            binding)

        match source with
        | :? Button as button when primary -> state.suppress_next_set_click <- Some button
        | _ -> ()

        captured || Option.isNone state.active
    | None -> false

let attach_mouse_handler (state: State) (control: Control) =
    control.MouseDown.Add(fun (event: MouseEventArgs) ->
        if try_capture_mouse state control event then
            event.Handled <- true
        elif not (is_editor_control control) then
            stop state
            state.focus_sink.Focus())

let attach_mouse_behavior (state: State) (control: Control) =
    attach_mouse_handler state control

    match control with
    | :? Container as container ->
        for child in container.Children do
            attach_mouse_handler state child
    | _ -> ()

let create () =
    let state =
        { focus_sink = new Drawable(CanFocus = true, Size = Size(1, 1))
          side_button_timer = new UITimer(Interval = SIDE_BUTTON_POLL_INTERVAL_SECONDS)
          active = None
          modifier_keys = []
          suppress_next_set_click = None
          disposed = false }

    state.side_button_timer.Elapsed.Add(fun (_: EventArgs) ->
        try
            match state.active with
            | Some active when active.button.HasFocus -> capture_key state PlatformBindings.try_side_mouse_binding
            | _ -> cancel state
        with error ->
            Debug.WriteLine $"RhinosCanFly binding capture timer: {error}"
            cancel state)

    state

let dispose (state: State) =
    if not state.disposed then
        cancel state
        state.disposed <- true
        state.side_button_timer.Dispose()
