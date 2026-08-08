module RhinosCanFly.BindingCapture

open System
open Eto.Drawing
open Eto.Forms

type Active = { field: TextBox; button: Button }

type State =
    { focus_sink: Drawable
      side_button_timer: UITimer
      mutable active: Active option
      mutable suppress_next_set_click: Button option }

[<Literal>]
let side_button_poll_interval_seconds = 0.015

let same_button (left: Button) (right: Button) = Object.ReferenceEquals(left, right)

let stop (state: State) =
    match state.active with
    | Some active -> active.button.Text <- "Set..."
    | None -> ()

    state.active <- None

    if state.side_button_timer.Started then
        state.side_button_timer.Stop()

let complete (state: State) (binding: string) =
    match state.active with
    | Some active ->
        active.field.Text <- binding
        stop state
    | None -> ()

let start (state: State) (field: TextBox) (button: Button) =
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
        | _ -> start state field setButton)

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
        match source with
        | :? Button as button when event.Buttons = MouseButtons.Primary -> state.suppress_next_set_click <- Some button
        | _ -> ()

        complete state binding
        true
    | _ -> false

let rec attach_mouse_behavior (state: State) (control: Control) =
    control.MouseDown.Add(fun (event: MouseEventArgs) ->
        if try_capture_mouse state control event then
            event.Handled <- true
        elif not (is_editor_control control) then
            stop state
            state.focus_sink.Focus())

    match control with
    | :? Container as container ->
        for child in container.Children do
            attach_mouse_behavior state child
    | _ -> ()

let create () =
    let state =
        { focus_sink = new Drawable(CanFocus = true, Size = Size(1, 1))
          side_button_timer = new UITimer(Interval = side_button_poll_interval_seconds)
          active = None
          suppress_next_set_click = None }

    state.side_button_timer.Elapsed.Add(fun (_: EventArgs) ->
        PlatformBindings.try_side_mouse_binding () |> Option.iter (complete state))

    state

let dispose (state: State) =
    stop state
    state.side_button_timer.Dispose()
