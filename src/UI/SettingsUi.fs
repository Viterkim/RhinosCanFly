module RhinosCanFly.SettingsUi

open System.Diagnostics
open System.Reflection
open Eto.Forms
open Rhino
open Rhino.UI

let report_error (message: string) =
    try
        RhinoApp.WriteLine message
    with error ->
        Debug.WriteLine $"{message}; output failed: {error.Message}"

let use_rhino_style_method =
    typeof<EtoExtensions>
        .GetMethod("UseRhinoStyle", BindingFlags.Public ||| BindingFlags.Static, null, [| typeof<Control> |], null)

let load_icon () =
    try
        let assembly = Assembly.GetExecutingAssembly()

        let stream =
            assembly.GetManifestResourceStream "RhinosCanFly.Resources.PluginIcon.ico"

        if isNull stream then
            None
        else
            use source = stream
            Some(new Eto.Drawing.Icon(source))
    with error ->
        Debug.WriteLine $"RhinosCanFly could not load the Options icon: {error.Message}"
        None

let use_rhino_style (control: Control) =
    if not (isNull use_rhino_style_method) then
        try
            use_rhino_style_method.Invoke(null, [| control :> obj |]) |> ignore
        with error ->
            Debug.WriteLine $"RhinosCanFly could not apply the Rhino UI style: {error.Message}"
