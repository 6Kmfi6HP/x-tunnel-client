param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [string]$CoreRepo = (Resolve-Path "$PSScriptRoot\..\..\x-tunnel").Path,
    [string]$Dotnet = ""
)

$ErrorActionPreference = "Stop"

$packageArgs = @(
    "-Configuration", $Configuration,
    "-Runtimes", $Runtime,
    "-CoreRepo", $CoreRepo,
    "-SkipInstaller",
    "-SkipSelfContained"
)

if (-not [string]::IsNullOrWhiteSpace($Dotnet)) {
    $packageArgs += @("-Dotnet", $Dotnet)
}

& (Join-Path $PSScriptRoot "package.ps1") @packageArgs
