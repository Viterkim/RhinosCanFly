param(
    [ValidateSet("Debug", "Release")]
    [string] $Configuration = "Release",
    [ValidateSet(8, 9)]
    [int] $RhinoVersion = 8,
    [switch] $Clean,
    [switch] $Preflight
)

$ErrorActionPreference = "Stop"
$projectRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$project = Join-Path $projectRoot "RhinosCanFly.fsproj"
$properties = @("-p:RhinosCanFlyPlatform=mac", "-p:RhinoMajorVersion=$RhinoVersion")

Push-Location $projectRoot
try {
    $sdkVersion = & dotnet --version
    if ($LASTEXITCODE -ne 0) { throw "The SDK selected by global.json is unavailable." }
    $framework = & dotnet msbuild $project -nologo @properties -getProperty:TargetFramework
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    $output = Join-Path $projectRoot "bin/mac/rh$RhinoVersion/$Configuration/$framework"
    Write-Host ".NET SDK: $sdkVersion ($((Get-Command dotnet).Source))"
    Write-Host "Managed target: Rhino $RhinoVersion, $framework"
    Write-Host "Output: $output"
    if ($Preflight) { return }
    $record = Join-Path $output ".verified-build.json"
    if (Test-Path -LiteralPath $record) { Remove-Item -LiteralPath $record }

    & dotnet tool restore --verbosity quiet
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

    & dotnet fantomas --check (Join-Path $projectRoot "src") (Join-Path $projectRoot "tools")
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

    & dotnet fsi (Join-Path $projectRoot "tools/check-source-style.fsx") -- (Join-Path $projectRoot "tools")
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

    if ($Clean) {
        & dotnet restore $project @properties
        if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

        & dotnet clean $project --configuration $Configuration @properties
        if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    }

    & dotnet build $project --configuration $Configuration @properties
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}
finally {
    Pop-Location
}
