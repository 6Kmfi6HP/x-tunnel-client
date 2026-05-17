param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [string]$CoreRepo = (Resolve-Path "$PSScriptRoot\..\..\x-tunnel").Path,
    [string]$Dotnet = "$env:USERPROFILE\.dotnet-sdk\dotnet.exe"
)

$ErrorActionPreference = "Stop"

$repo = Resolve-Path "$PSScriptRoot\.."
$artifacts = Join-Path $repo "artifacts"
$publish = Join-Path $artifacts "publish\$Runtime"
$bundle = Join-Path $artifacts "x-tunnel-client-$Runtime"
$zip = Join-Path $artifacts "x-tunnel-client-$Runtime.zip"
$coreExe = Join-Path $CoreRepo "build\x-tunnel.exe"

New-Item -ItemType Directory -Force -Path $artifacts | Out-Null

Push-Location $CoreRepo
try {
    go build -o $coreExe .\cmd\x-tunnel
    go test ./...
}
finally {
    Pop-Location
}

& $Dotnet test (Join-Path $repo "XTunnelClient.sln")
& $Dotnet publish (Join-Path $repo "src\XTunnelClient.App\XTunnelClient.App.csproj") `
    -c $Configuration `
    -r $Runtime `
    --self-contained false `
    -o $publish

if (Test-Path $bundle) {
    Remove-Item -Recurse -Force $bundle
}
New-Item -ItemType Directory -Force -Path (Join-Path $bundle "core") | Out-Null
Copy-Item -Recurse -Force (Join-Path $publish "*") $bundle
Copy-Item -Force $coreExe (Join-Path $bundle "core\x-tunnel.exe")

$coreHash = (Get-FileHash (Join-Path $bundle "core\x-tunnel.exe") -Algorithm SHA256).Hash.ToLowerInvariant()
$guiExe = Join-Path $bundle "XTunnelClient.App.exe"
$guiHash = (Get-FileHash $guiExe -Algorithm SHA256).Hash.ToLowerInvariant()
@{
    version = "0.1.0-dev"
    runtime = $Runtime
    gui_sha256 = $guiHash
    core_sha256 = $coreHash
    built_at = (Get-Date).ToUniversalTime().ToString("O")
} | ConvertTo-Json | Set-Content -Encoding UTF8 (Join-Path $bundle "version.json")

if (Test-Path $zip) {
    Remove-Item -Force $zip
}
Compress-Archive -Path (Join-Path $bundle "*") -DestinationPath $zip
Write-Host "Portable artifact: $zip"
