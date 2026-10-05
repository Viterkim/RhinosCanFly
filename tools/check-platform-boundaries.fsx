#load "source-lexer.fsx"

open System
open System.IO
open System.Text.RegularExpressions
open SourceLexer

let source_root = fsi.CommandLineArgs |> Array.last |> Path.GetFullPath
let platform_root = Path.Combine(source_root, "Platform") |> Path.GetFullPath

type Rule = { name: string; pattern: Regex }

let rule (name: string) (pattern: string) =
    { name = name
      pattern = Regex(pattern, RegexOptions.Compiled) }

let windows_details =
    [ rule "System.Windows.Forms" @"\bSystem\.Windows\.Forms\b"
      rule "Platform.Win" @"\bPlatform\.Win\b"
      rule "Win32" @"\bWin32(?:Native)?\b" ]

let mac_details =
    [ rule "Platform.Mac" @"\bPlatform\.Mac\b"
      rule "MacNative" @"\bMacNative\b"
      rule "MacNavigationInput" @"\bMacNavigationInput\b" ]

let linux_details =
    [ rule "Platform.Linux" @"\bPlatform\.Linux\b"
      rule "WaylandInput" @"\bWaylandInput\b" ]

let forbidden =
    [ rule "System.Drawing" @"\bSystem\.Drawing\b(?!\.(?:Color|PointF?|RectangleF?|SizeF?)\b)"
      rule "DllImport" @"\bDllImport\b"
      rule "nativeint" @"\bnativeint\b"
      rule "unativeint" @"\bunativeint\b"
      rule "window handle" @"\.Handle\b"
      rule "screen rectangle" @"\.ScreenRectangle\b" ]
    @ windows_details
    @ mac_details
    @ linux_details

let is_inside (directory: string) (path: string) =
    let prefix =
        directory.TrimEnd(Path.DirectorySeparatorChar)
        + string Path.DirectorySeparatorChar

    path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)

let violations =
    Directory.EnumerateFiles(source_root, "*.fs", SearchOption.AllDirectories)
    |> Seq.collect (fun (path: string) ->
        let rules =
            if not (is_inside platform_root path) then
                forbidden
            elif is_inside (Path.Combine(platform_root, "Win")) path then
                mac_details @ linux_details
            elif is_inside (Path.Combine(platform_root, "Mac")) path then
                windows_details @ linux_details
            elif is_inside (Path.Combine(platform_root, "Linux")) path then
                windows_details @ mac_details
            else
                windows_details @ mac_details @ linux_details

        code_only (File.ReadAllText path)
        |> fun (code: string) -> code.Replace("\r\n", "\n").Split '\n'
        |> Seq.mapi (fun (index: int) (line: string) -> index + 1, line)
        |> Seq.collect (fun (line_number: int, line: string) ->
            rules
            |> Seq.choose (fun (rule: Rule) ->
                if rule.pattern.IsMatch line then
                    Some $"{path}({line_number}): platform implementation detail '{rule.name}' crossed its boundary"
                else
                    None)))
    |> Seq.toList

for violation in violations do
    Console.Error.WriteLine violation

if not (List.isEmpty violations) then
    Environment.Exit 1
