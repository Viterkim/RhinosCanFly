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
      mutable suppress_next_set_click: Button option
      mutable disposed: bool }

[<Literal>]
let SIDE_BUTTON_POLL_INTERVAL_SECONDS = 0.015

let same_button (left: Button) (right: Button) = Object.ReferenceEquals(left, right)

let stop (state: State) =
    match state.active with
    | Some active ->
        InputDebugTrace.write "BindingCapture stop active=true"
        active.button.Text <- "Set..."
    | None -> ()

    state.active <- None

    if not state.disposed && state.side_button_timer.Started then
        state.side_button_timer.Stop()

let cancel (state: State) =
    InputDebugTrace.write $"BindingCapture cancel active={state.active.IsSome}"
    stop state
    state.suppress_next_set_click <- None

let complete (state: State) (binding: string) =
    match state.active with
    | Some active ->
        InputDebugTrace.write $"BindingCapture complete binding={binding}"
        active.field.Text <- binding
        stop state
    | None -> ()

let start (state: State) (field: TextBox) (button: Button) =
    if not state.disposed then
        InputDebugTrace.write $"BindingCapture start previous-active={state.active.IsSome}"
        stop state
        state.active <- Some { field = field; button = button }
        button.Text <- "Press..."
        button.Focus()
        state.side_button_timer.Start()

let editor (state: State) (field: TextBox) (defaultValue: string) =
    let setButton = new Button(Text = "Set...", Width = 62, Height = 24)
    let defaultButton = new Button(Text = "Default", Width = 66, Height = 24)
    let panel = new TableLayout(Spacing = Size(6, 0))

    panel.Rows.Add(
        SettingsLayout.row
            [ new TableCell(field, true)
              new TableCell(setButton, false)
              new TableCell(defaultButton, false) ]
    )

    setButton.Click.Add(fun (_: EventArgs) ->
        match state.suppress_next_set_click with
        | Some suppressed when same_button suppressed setButton -> state.suppress_next_set_click <- None
        | Some _ ->
            state.suppress_next_set_click <- None
            start state field setButton
        | None -> start state field setButton)

    setButton.KeyDown.Add(fun (event: KeyEventArgs) ->
        match state.active with
        | Some active when same_button active.button setButton ->
            event.Handled <- true

            if not (PlatformBindings.is_modifier_key event.Key) then
                PlatformBindings.binding_from_key event.Key event.Modifiers |> complete state
        | _ -> ())

    setButton.KeyUp.Add(fun (event: KeyEventArgs) ->
        match state.active with
        | Some active when same_button active.button setButton ->
            event.Handled <- true

            if PlatformBindings.is_modifier_key event.Key then
                PlatformBindings.key_name event.Key |> complete state
        | _ -> ())

    defaultButton.Click.Add(fun (_: EventArgs) -> field.Text <- defaultValue)
    panel :> Control

let is_editor_control (control: Control) =
    match control with
    | :? TextBox
    | :? TextArea
    | :? Button
    | :? CheckBox
    | :? DropDown -> true
    | _ -> false

let try_capture_mouse (state: State) (source: Control) (event: MouseEventArgs) =
    match state.active, PlatformBindings.binding_from_mouse event.Buttons event.Modifiers with
    | Some _, Some binding ->
        InputDebugTrace.write
            $"BindingCapture mouse binding={binding} source={source.GetType().Name} buttons={event.Buttons} modifiers={event.Modifiers}"

        match source with
        | :? Button as button when event.Buttons = MouseButtons.Primary -> state.suppress_next_set_click <- Some button
        | _ -> ()

        complete state binding
        true
    | _ -> false

let attach_mouse_handler (state: State) (control: Control) =
    control.MouseDown.Add(fun (event: MouseEventArgs) ->
        InputDebugTrace.write
            $"BindingCapture control MouseDown source={control.GetType().Name} active={state.active.IsSome} buttons={event.Buttons} modifiers={event.Modifiers}"

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
    InputDebugTrace.write "BindingCapture create begin"

    let state =
        { focus_sink = new Drawable(CanFocus = true, Size = Size(1, 1))
          side_button_timer = new UITimer(Interval = SIDE_BUTTON_POLL_INTERVAL_SECONDS)
          active = None
          suppress_next_set_click = None
          disposed = false }

    state.side_button_timer.Elapsed.Add(fun (_: EventArgs) ->
        try
            PlatformBindings.try_side_mouse_binding () |> Option.iter (complete state)
        with error ->
            InputDebugTrace.write $"BindingCapture timer exception={error}"
            Debug.WriteLine $"RhinosCanFly binding capture timer: {error}"
            cancel state)

    InputDebugTrace.write "BindingCapture create end"
    state

let dispose (state: State) =
    if not state.disposed then
        InputDebugTrace.write $"BindingCapture dispose begin active={state.active.IsSome}"
        cancel state
        state.disposed <- true
        state.side_button_timer.Dispose()
        InputDebugTrace.write "BindingCapture dispose end"
