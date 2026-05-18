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

function Invoke-Checked {
    param(
        [string]$File,
        [string[]]$Arguments
    )

    $Output = & $File @Arguments 2>&1
    $ExitCode = $LASTEXITCODE
    if ($ExitCode -ne 0) {
        throw "Command failed with exit code ${ExitCode}: $File $($Arguments -join ' ')`n$($Output -join "`n")"
    }
    return $Output
}

function Get-FreeTcpPort {
    $Listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, 0)
    $Listener.Start()
    try {
        return ([System.Net.IPEndPoint]$Listener.LocalEndpoint).Port
    }
    finally {
        $Listener.Stop()
    }
}

function Wait-Until {
    param(
        [scriptblock]$Condition,
        [int]$TimeoutSeconds,
        [string]$Message
    )

    $Deadline = [DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds)
    $Last = $null
    while ([DateTimeOffset]::UtcNow -lt $Deadline) {
        try {
            $Value = & $Condition
            if ($Value) {
                return $Value
            }
        }
        catch {
            $Last = $_
        }
        Start-Sleep -Milliseconds 200
    }

    if ($Last) {
        throw "$Message Last error: $($Last.Exception.Message)"
    }
    throw $Message
}

function Test-CanRunRuntime {
    param([string]$Runtime)

    $Architecture = [System.Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture
    switch ($Runtime) {
        "win-x64" { return $Architecture -eq [System.Runtime.InteropServices.Architecture]::X64 }
        "win-arm64" { return $Architecture -eq [System.Runtime.InteropServices.Architecture]::Arm64 }
        default { return $false }
    }
}

function Assert-CoreGuiContract {
    param([string]$CoreExe)

    $Temp = Join-Path ([System.IO.Path]::GetTempPath()) ("xtunnel-release-core-contract-" + [Guid]::NewGuid().ToString("N"))
    New-Item -ItemType Directory -Force -Path $Temp | Out-Null
    $Process = $null
    try {
        $SocksPort = Get-FreeTcpPort
        do {
            $HttpPort = Get-FreeTcpPort
        } while ($HttpPort -eq $SocksPort)

        $ConfigPath = Join-Path $Temp "client.json"
        $ReadyPath = Join-Path $Temp "ready.json"
        $TokenPath = Join-Path $Temp "token"
        $ConfigJson = @"
{
  "listen": "socks5://127.0.0.1:$SocksPort,http://127.0.0.1:$HttpPort",
  "forward": "ws://127.0.0.1:18080/tunnel",
  "token": "contract-test-token",
  "connections": 1,
  "fallback": true,
  "metrics": "127.0.0.1:0"
}
"@
        [System.IO.File]::WriteAllText($ConfigPath, $ConfigJson, [System.Text.UTF8Encoding]::new($false))

        Invoke-Checked -File $CoreExe -Arguments @("-version") | Out-Null
        Invoke-Checked -File $CoreExe -Arguments @("-check-config", $ConfigPath) | Out-Null
        Invoke-Checked -File $CoreExe -Arguments @("-format-config", $ConfigPath) | Out-Null

        $StartInfo = [System.Diagnostics.ProcessStartInfo]::new()
        $StartInfo.FileName = $CoreExe
        $StartInfo.WorkingDirectory = Split-Path -Parent $CoreExe
        $StartInfo.UseShellExecute = $false
        $StartInfo.CreateNoWindow = $true
        $StartInfo.RedirectStandardOutput = $true
        $StartInfo.RedirectStandardError = $true
        foreach ($Argument in @("-config", $ConfigPath, "-control", "127.0.0.1:0", "-ready-file", $ReadyPath, "-control-token-file", $TokenPath)) {
            $StartInfo.ArgumentList.Add($Argument)
        }

        $Process = [System.Diagnostics.Process]::Start($StartInfo)
        if ($null -eq $Process) {
            throw "Failed to start core contract smoke process."
        }

        $Ready = Wait-Until -TimeoutSeconds 10 -Message "Timed out waiting for bundled core ready file." -Condition {
            if ($Process.HasExited) {
                $StdErr = $Process.StandardError.ReadToEnd()
                $StdOut = $Process.StandardOutput.ReadToEnd()
                throw "Core exited before ready. stdout=$StdOut stderr=$StdErr"
            }
            if (Test-Path -LiteralPath $ReadyPath) {
                return Get-Content -Raw -LiteralPath $ReadyPath | ConvertFrom-Json
            }
            return $null
        }

        $Token = (Get-Content -Raw -LiteralPath $TokenPath).Trim()
        $Headers = @{ Authorization = "Bearer $Token" }
        $VersionInfo = Invoke-RestMethod -Method Get -Uri "$($Ready.control_url)/v1/version" -TimeoutSec 5
        if ($null -eq $VersionInfo.control_api_version -or [int]$VersionInfo.control_api_version -lt 1) {
            throw "Core /v1/version is missing control_api_version >= 1."
        }
        $Capabilities = @($VersionInfo.capabilities)
        foreach ($Required in @("status", "logs", "stats", "config_check", "config_format", "runtime_stop")) {
            if ($Capabilities -notcontains $Required) {
                throw "Core /v1/version is missing required capability '$Required'."
            }
        }

        Invoke-RestMethod -Method Post -Uri "$($Ready.control_url)/v1/runtime/stop" -Headers $Headers -TimeoutSec 5 | Out-Null
        if (-not $Process.WaitForExit(5000)) {
            throw "Core did not stop after /v1/runtime/stop."
        }
    }
    finally {
        if ($null -ne $Process -and -not $Process.HasExited) {
            $Process.Kill($true)
            $Process.WaitForExit()
        }
        Remove-Item -LiteralPath $Temp -Recurse -Force -ErrorAction SilentlyContinue
    }
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

    if (Test-CanRunRuntime -Runtime ([string]$Asset.runtime)) {
        $ExtractDir = Join-Path ([System.IO.Path]::GetTempPath()) ("xtunnel-release-verify-" + [Guid]::NewGuid().ToString("N"))
        try {
            [System.IO.Compression.ZipFile]::ExtractToDirectory($ZipPath, $ExtractDir)
            Assert-CoreGuiContract -CoreExe (Join-Path $ExtractDir "core\x-tunnel.exe")
        }
        finally {
            Remove-Item -LiteralPath $ExtractDir -Recurse -Force -ErrorAction SilentlyContinue
        }
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
