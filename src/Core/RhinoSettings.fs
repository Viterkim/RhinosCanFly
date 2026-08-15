module RhinosCanFly.RhinoSettings

open System
open System.Reflection
open Rhino

let boolean_setting_getter (typeName: string) (propertyName: string) =
    try
        let settingsType = typeof<RhinoApp>.Assembly.GetType typeName

        if isNull settingsType then
            None
        else
            let property =
                settingsType.GetProperty(propertyName, BindingFlags.Public ||| BindingFlags.Static)

            if isNull property || property.PropertyType <> typeof<bool> || not property.CanRead then
                None
            else
                let method = property.GetGetMethod()

                if isNull method then
                    None
                else
                    Some(Delegate.CreateDelegate(typeof<Func<bool>>, method) :?> Func<bool>)
    with _ ->
        None

let rotate_view_around_gumball_getter =
    match boolean_setting_getter "Rhino.ApplicationSettings.GumballSettings" "RotateViewAroundGumball" with
    | Some getter -> Some getter
    | None -> boolean_setting_getter "Rhino.ApplicationSettings.ViewSettings" "RotateViewAroundAutogumball"

let rotate_view_around_gumball () =
    match rotate_view_around_gumball_getter with
    | Some getter -> getter.Invoke()
    | None -> false
