module RhinosCanFly.RhinoSettings

open System.Reflection
open Rhino

let boolean_setting (typeName: string) (propertyName: string) =
    let settingsType = typeof<RhinoApp>.Assembly.GetType typeName

    if isNull settingsType then
        None
    else
        let property =
            settingsType.GetProperty(propertyName, BindingFlags.Public ||| BindingFlags.Static)

        if isNull property || property.PropertyType <> typeof<bool> then
            None
        else
            Some(unbox<bool> (property.GetValue(null, null)))

let rotate_view_around_gumball () =
    [ "Rhino.ApplicationSettings.GumballSettings", "RotateViewAroundGumball"
      "Rhino.ApplicationSettings.ViewSettings", "RotateViewAroundAutogumball" ]
    |> List.tryPick (fun (typeName: string, propertyName: string) -> boolean_setting typeName propertyName)
    |> Option.defaultValue false
