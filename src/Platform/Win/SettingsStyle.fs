module RhinosCanFly.PlatformSettingsStyle

open System
open Eto.Forms

let center_text (field: TextBox) =
    let native = field.ControlObject

    if not (isNull native) then
        let property = native.GetType().GetProperty "VerticalContentAlignment"

        if not (isNull property) && property.PropertyType.IsEnum then
            property.SetValue(native, Enum.Parse(property.PropertyType, "Center"))
