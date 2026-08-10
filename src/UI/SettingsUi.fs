namespace RhinosCanFly

open System.Diagnostics
open System.Reflection
open Eto.Forms
open Rhino.UI

module SettingsUi =
    let useRhinoStyleMethod =
        typeof<EtoExtensions>
            .GetMethod("UseRhinoStyle", BindingFlags.Public ||| BindingFlags.Static, null, [| typeof<Control> |], null)

    let load_icon () =
        let assembly = Assembly.GetExecutingAssembly()

        let stream =
            assembly.GetManifestResourceStream "RhinosCanFly.Resources.PluginIcon.ico"

        if isNull stream then
            None
        else
            use source = stream
            Some(new Eto.Drawing.Icon(source))

    let use_rhino_style (control: Control) =
        if not (isNull useRhinoStyleMethod) then
            try
                useRhinoStyleMethod.Invoke(null, [| control :> obj |]) |> ignore
            with error ->
                Debug.WriteLine $"RhinosCanFly could not apply the Rhino UI style: {error.Message}"
