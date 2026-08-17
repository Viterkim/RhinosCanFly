function Resolve-RhinoCommandName {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Name
    )

    $trimmed = $Name.Trim()

    if ($trimmed -notmatch '^[A-Za-z][A-Za-z0-9_]{0,62}$') {
        throw "Use at most 63 letters, numbers, or underscores, starting with a letter."
    }

    $commandName = $trimmed.Substring(0, 1).ToUpperInvariant() + $trimmed.Substring(1)

    [PSCustomObject]@{
        CommandName = $commandName
        SourceName = $commandName
        RelativePath = "src/Commands/$commandName.fs"
        ModuleSuffix = "Commands.$commandName"
        TypeName = "${commandName}Command"
    }
}
