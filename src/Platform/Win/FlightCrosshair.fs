namespace RhinosCanFly

open System
open System.Drawing
open Rhino.Display

module Crosshair =
    let scaled_dimensions (config: CrosshairConfig) (scale: float) (extent: int) =
        let gap = max 1. (Math.Round(float config.gap * scale))

        if extent <= 0 || gap > 2. * float extent then
            ValueNone
        else
            let length =
                max 1 (int (min (float extent) (Math.Round(float config.arm_length * scale))))

            let border = max 1 (int (Math.Round scale))
            // Gap and stroke need matching parity to share a pixel-aligned centre.
            let gap = int gap
            let gap = gap + (if gap % 2 = border % 2 then 0 else 1)
            ValueSome(struct (length, gap, border))

    let arm_rectangles (arm: int) (length: int) (gap: int) (border: int) (x: int) (y: int) =
        let negative_gap = gap / 2
        let positive_gap = gap - negative_gap
        let outer_length = length + 2 * border
        let width = 3 * border

        let outer =
            match arm with
            | 0 -> Rectangle(x - negative_gap - outer_length, y - width / 2, outer_length, width)
            | 1 -> Rectangle(x + positive_gap, y - width / 2, outer_length, width)
            | 2 -> Rectangle(x - width / 2, y - negative_gap - outer_length, width, outer_length)
            | _ -> Rectangle(x - width / 2, y + positive_gap, width, outer_length)

        let inner =
            Rectangle(outer.X + border, outer.Y + border, outer.Width - 2 * border, outer.Height - 2 * border)

        struct (outer, inner)

type FlightCrosshair(state: FlyState) =
    inherit DisplayConduit()

    let config = state.config.behavior.crosshair
    let color = Color.FromArgb(config.red, config.green, config.blue)

    override _.DrawForeground(event: DrawEventArgs) =
        if FlyState.is_running state && event.Viewport.Id = state.host_identity.viewport_id then
            let point = ViewTarget.viewport_center event.Viewport
            let scale = float event.Display.DpiScale
            let bounds = event.Viewport.Bounds
            let extent = max bounds.Width bounds.Height

            match Crosshair.scaled_dimensions config scale extent with
            | ValueNone -> ()
            | ValueSome(struct (length, gap, border)) ->
                for arm = 0 to 3 do
                    let struct (outer, inner) =
                        Crosshair.arm_rectangles arm length gap border point.x point.y

                    event.Display.Draw2dRectangle(outer, Color.Black, 0, Color.Black)
                    event.Display.Draw2dRectangle(inner, color, 0, color)
