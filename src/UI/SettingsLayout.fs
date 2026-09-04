module RhinosCanFly.SettingsLayout

open Eto.Drawing
open Eto.Forms

[<Literal>]
let ITEM_LABEL_WIDTH = 155

let row (cells: TableCell list) =
    let result = new TableRow()

    for cell in cells do
        result.Cells.Add cell

    result

let full_width (control: Control) = row [ new TableCell(control, true) ]

let item (label: string) (control: Control) =
    let caption = new Label(Text = label, Width = ITEM_LABEL_WIDTH)
    let result = new TableLayout(Spacing = Size(8, 0))

    result.Rows.Add(row [ new TableCell(caption, false); new TableCell(control, true) ])
    result :> Control

let fixed_item (label_width: int) (control_width: int) (label: string) (control: Control) =
    control.Width <- control_width

    let caption = new Label(Text = label, Width = label_width)
    let result = new TableLayout(Spacing = Size(8, 0))

    result.Rows.Add(row [ new TableCell(caption, false); new TableCell(control, false) ])
    result :> Control

let grid (columns: int) (controls: Control list) =
    if columns <= 0 then
        invalidArg (nameof columns) "The column count must be positive."

    let result = new TableLayout(Spacing = Size(16, 4))

    for controls_in_row in List.chunkBySize columns controls do
        let empty_cells =
            List.init (columns - controls_in_row.Length) (fun (_: int) -> new TableCell(new Panel(), true))

        let cells =
            controls_in_row
            |> List.map (fun (control: Control) -> new TableCell(control, true))
            |> fun (populated_cells: TableCell list) -> populated_cells @ empty_cells

        result.Rows.Add(row cells)

    result :> Control

let heading (text: string) =
    let label =
        new Label(Text = text, Font = SystemFonts.Bold(System.Nullable 15.f, FontDecoration.None))

    new Panel(Content = label, Padding = Padding(0, 10, 0, 2)) :> Control

let subheading (text: string) =
    let label =
        new Label(Text = text, Font = SystemFonts.Bold(System.Nullable 10.f, FontDecoration.None))

    new Panel(Content = label, Padding = Padding(0, 4, 0, 0)) :> Control

let spacer (height: int) = new Panel(Height = height) :> Control

let note (text: string) =
    new Label(Text = text, Wrap = WrapMode.Word) :> Control
