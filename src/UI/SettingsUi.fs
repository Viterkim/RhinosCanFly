namespace RhinosCanFly

open System.Reflection
open Eto.Forms
open Rhino.UI

module SettingsUi =
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
        let method =
            typeof<EtoExtensions>
                .GetMethod(
                    "UseRhinoStyle",
                    BindingFlags.Public ||| BindingFlags.Static,
                    null,
                    [| typeof<Control> |],
                    null
                )

        if not (isNull method) then
            method.Invoke(null, [| control :> obj |]) |> ignore
