namespace RhinosCanFly

open System
open Eto.Drawing
open Rhino.Display

[<Struct>]
type CursorPosition = CursorPosition of PointF

[<Struct>]
type ViewportHostIdentity =
    { document_serial_number: uint32
      view_serial_number: uint32
      viewport_id: Guid
      view: RhinoView
      view_window: nativeint
      floating: bool
      sdk_window: bool
      window: nativeint }
