[CmdletBinding()]
param(
    [string]$Manifest = "",

    [string]$Dist = ""
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$Repo = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
if ([string]::IsNullOrWhiteSpace($Dist)) {
    $Dist = Join-Path $Repo "artifacts\dist"
}
$Dist = [System.IO.Path]::GetFullPath($Dist)
if ([string]::IsNullOrWhiteSpace($Manifest)) {
    $Manifest = Join-Path $Dist "release-manifest.json"
}
$Manifest = [System.IO.Path]::GetFullPath($Manifest)

function Get-FileSha256 {
    param([string]$Path)
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Assert-ZipPayload {
    param(
        [string]$ZipPath,
        [object]$Asset
    )

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $Archive = [System.IO.Compression.ZipFile]::OpenRead($ZipPath)
    try {
        $Names = @($Archive.Entries | ForEach-Object { $_.FullName.Replace("\", "/") })
        foreach ($Required in @("XTunnelClient.App.exe", "core/x-tunnel.exe", "version.json")) {
            if ($Names -notcontains $Required) {
                throw "Portable package '$($Asset.name)' is missing required entry '$Required'."
            }
        }
    }
    finally {
        $Archive.Dispose()
    }
}

function Assert-MsiPayload {
    param([string]$MsiPath)

    $Bytes = [System.IO.File]::ReadAllBytes($MsiPath)
    $Expected = [byte[]](0xD0, 0xCF, 0x11, 0xE0)
    for ($Index = 0; $Index -lt $Expected.Length; $Index++) {
        if ($Bytes[$Index] -ne $Expected[$Index]) {
            throw "Installer does not look like a Windows Installer MSI compound file: $MsiPath"
        }
    }
}

if (-not (Test-Path -LiteralPath $Manifest)) {
    throw "Release manifest was not found: $Manifest"
}

$Data = Get-Content -Raw -LiteralPath $Manifest | ConvertFrom-Json
if ($Data.schema -ne 1) {
    throw "Unsupported release manifest schema: $($Data.schema)"
}
if (-not $Data.assets -or $Data.assets.Count -eq 0) {
    throw "Release manifest has no assets."
}

$ChecksumPath = Join-Path $Dist "SHA256SUMS"
if (-not (Test-Path -LiteralPath $ChecksumPath)) {
    throw "SHA256SUMS was not found: $ChecksumPath"
}

$ChecksumByPath = @{}
foreach ($Line in Get-Content -LiteralPath $ChecksumPath) {
    if ([string]::IsNullOrWhiteSpace($Line)) {
        continue
    }
    if ($Line -notmatch '^([0-9a-fA-F]{64})\s+(.+)$') {
        throw "Invalid checksum line: $Line"
    }
    $ChecksumByPath[$Matches[2].Trim()] = $Matches[1].ToLowerInvariant()
}

foreach ($Asset in $Data.assets) {
    $AssetPath = Join-Path $Dist $Asset.path
    if (-not (Test-Path -LiteralPath $AssetPath)) {
        throw "Manifest asset does not exist: $($Asset.path)"
    }

    $ActualSize = (Get-Item -LiteralPath $AssetPath).Length
    if ([int64]$Asset.size -ne $ActualSize) {
        throw "Manifest size mismatch for $($Asset.path): manifest=$($Asset.size), actual=$ActualSize"
    }

    $ActualHash = Get-FileSha256 -Path $AssetPath
    if ($ActualHash -ne [string]$Asset.sha256) {
        throw "Manifest SHA256 mismatch for $($Asset.path)"
    }
    if (-not $ChecksumByPath.ContainsKey([string]$Asset.path)) {
        throw "SHA256SUMS is missing $($Asset.path)"
    }
    if ($ChecksumByPath[[string]$Asset.path] -ne $ActualHash) {
        throw "SHA256SUMS mismatch for $($Asset.path)"
    }

    switch ([string]$Asset.kind) {
        "portable" { Assert-ZipPayload -ZipPath $AssetPath -Asset $Asset }
        "installer" { Assert-MsiPayload -MsiPath $AssetPath }
        default { throw "Unknown asset kind: $($Asset.kind)" }
    }
}

if (-not $ChecksumByPath.ContainsKey("release-manifest.json")) {
    throw "SHA256SUMS is missing release-manifest.json"
}

$ManifestHash = Get-FileSha256 -Path $Manifest
if ($ChecksumByPath["release-manifest.json"] -ne $ManifestHash) {
    throw "SHA256SUMS mismatch for release-manifest.json"
}

Write-Host "Release verification passed for $($Data.assets.Count) assets."
