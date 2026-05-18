[CmdletBinding()]
param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",

    [string[]]$Runtimes = @(),

    [string]$Version = "",

    [string]$CoreRepo = "",

    [string]$Output = "",

    [string]$Dotnet = "",

    [switch]$SkipTests,

    [switch]$SkipInstaller,

    [switch]$SkipFrameworkDependent,

    [switch]$SkipSelfContained,

    [switch]$NoClean,

    [switch]$SkipMsiValidation,

    [switch]$Sign,

    [string]$SignCertificatePath = "",

    [string]$SignCertificatePasswordEnv = "WINDOWS_SIGNING_CERT_PASSWORD",

    [string]$TimestampUrl = "http://timestamp.digicert.com"
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$Repo = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$ConfigPath = Join-Path $Repo "eng\release.json"
$ReleaseConfig = Get-Content -Raw -LiteralPath $ConfigPath | ConvertFrom-Json

if ($Runtimes.Count -eq 0) {
    $Runtimes = @($ReleaseConfig.runtimes)
}
if ([string]::IsNullOrWhiteSpace($CoreRepo)) {
    $CoreRepo = Join-Path $Repo "..\x-tunnel"
}
if ([string]::IsNullOrWhiteSpace($Output)) {
    $Output = Join-Path $Repo "artifacts\dist"
}

function Resolve-DotnetCli {
    param([string]$Requested)

    if (-not [string]::IsNullOrWhiteSpace($Requested)) {
        if (-not (Test-Path -LiteralPath $Requested)) {
            throw "Requested dotnet CLI was not found: $Requested"
        }
        return (Resolve-Path -LiteralPath $Requested).Path
    }

    $LocalDotnet = Join-Path $env:USERPROFILE ".dotnet-sdk\dotnet.exe"
    if (Test-Path -LiteralPath $LocalDotnet) {
        return (Resolve-Path -LiteralPath $LocalDotnet).Path
    }

    $Command = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($null -eq $Command) {
        throw "dotnet CLI was not found. Install .NET SDK $($ReleaseConfig.dotnetSdk) or pass -Dotnet."
    }
    return $Command.Source
}

function Invoke-Tool {
    param(
        [string]$File,
        [string[]]$Arguments,
        [string]$WorkingDirectory = $Repo
    )

    Push-Location $WorkingDirectory
    try {
        Write-Host ">$File $($Arguments -join ' ')"
        $CommandOutput = & $File @Arguments 2>&1
        $ExitCode = $LASTEXITCODE
        foreach ($Line in $CommandOutput) {
            Write-Host $Line
        }
        if ($ExitCode -ne 0) {
            throw "Command failed with exit code ${ExitCode}: $File $($Arguments -join ' ')"
        }
    }
    finally {
        Pop-Location
    }
}

function Get-GitOutput {
    param(
        [string]$WorkingDirectory,
        [string[]]$Arguments
    )

    $OutputLines = & git -C $WorkingDirectory @Arguments 2>$null
    if ($LASTEXITCODE -ne 0) {
        return ""
    }
    return ($OutputLines -join "`n").Trim()
}

function Get-ReleaseVersion {
    if (-not [string]::IsNullOrWhiteSpace($Version)) {
        return $Version.Trim()
    }

    $Describe = Get-GitOutput -WorkingDirectory $Repo -Arguments @("describe", "--tags", "--always", "--dirty")
    if (-not [string]::IsNullOrWhiteSpace($Describe)) {
        return $Describe
    }
    return "0.0.0-dev"
}

function Convert-ToPackageVersion {
    param([string]$RawVersion)

    $Value = $RawVersion.Trim()
    if ($Value.StartsWith("v", [StringComparison]::OrdinalIgnoreCase)) {
        $Value = $Value.Substring(1)
    }

    if ($Value -match '^[0-9]+\.[0-9]+\.[0-9]+(-[0-9A-Za-z][0-9A-Za-z.-]*)?(\+[0-9A-Za-z][0-9A-Za-z.-]*)?$') {
        return $Value
    }

    $SafeSuffix = ($Value -replace '[^0-9A-Za-z.-]+', '-').Trim("-")
    if ([string]::IsNullOrWhiteSpace($SafeSuffix)) {
        $SafeSuffix = "dev"
    }
    return "0.0.0-$SafeSuffix"
}

function Convert-ToMsiVersion {
    param([string]$PackageVersion)

    $Core = ($PackageVersion -split '[-+]')[0]
    if ($Core -notmatch '^[0-9]+\.[0-9]+\.[0-9]+$') {
        return "0.0.0"
    }
    return $Core
}

function Get-GoArch {
    param([string]$Runtime)

    switch ($Runtime) {
        "win-x64" { return "amd64" }
        "win-arm64" { return "arm64" }
        default { throw "Unsupported runtime '$Runtime'. Supported runtimes: win-x64, win-arm64." }
    }
}

function Get-WixArch {
    param([string]$Runtime)

    switch ($Runtime) {
        "win-x64" { return "x64" }
        "win-arm64" { return "arm64" }
        default { throw "Unsupported WiX runtime '$Runtime'." }
    }
}

function Assert-SafeOutputPath {
    param([string]$Path)

    $Full = [System.IO.Path]::GetFullPath($Path)
    $ArtifactsRoot = [System.IO.Path]::GetFullPath((Join-Path $Repo "artifacts"))
    $RepoRoot = [System.IO.Path]::GetFullPath($Repo)

    if (-not ($Full.StartsWith($ArtifactsRoot + [System.IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or $Full -eq $ArtifactsRoot)) {
        throw "Refusing to clean output outside repository artifacts directory: $Full"
    }
    if (-not $ArtifactsRoot.StartsWith($RepoRoot + [System.IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Resolved artifacts path is not under repository root: $ArtifactsRoot"
    }
}

function New-CleanDirectory {
    param([string]$Path)

    Assert-SafeOutputPath -Path $Path
    if ((Test-Path -LiteralPath $Path) -and -not $NoClean) {
        Remove-Item -LiteralPath $Path -Recurse -Force
    }
    New-Item -ItemType Directory -Force -Path $Path | Out-Null
}

function Copy-PublishToBundle {
    param(
        [string]$PublishDir,
        [string]$BundleDir,
        [string]$CoreExe
    )

    New-CleanDirectory -Path $BundleDir
    New-Item -ItemType Directory -Force -Path (Join-Path $BundleDir "core") | Out-Null
    Copy-Item -Path (Join-Path $PublishDir "*") -Destination $BundleDir -Recurse -Force
    Copy-Item -LiteralPath $CoreExe -Destination (Join-Path $BundleDir "core\x-tunnel.exe") -Force
}

function Get-FileSha256 {
    param([string]$Path)

    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Resolve-SignTool {
    $Command = Get-Command signtool.exe -ErrorAction SilentlyContinue
    if ($null -ne $Command) {
        return $Command.Source
    }

    $KitRoot = "${env:ProgramFiles(x86)}\Windows Kits\10\bin"
    if (Test-Path -LiteralPath $KitRoot) {
        $Candidate = Get-ChildItem -LiteralPath $KitRoot -Recurse -Filter signtool.exe -ErrorAction SilentlyContinue |
            Where-Object { $_.FullName -match '\\x64\\signtool\.exe$' } |
            Sort-Object FullName -Descending |
            Select-Object -First 1
        if ($null -ne $Candidate) {
            return $Candidate.FullName
        }
    }

    throw "signtool.exe was not found. Install the Windows SDK or run without -Sign."
}

function Invoke-CodeSign {
    param([string]$Path)

    if (-not $Sign) {
        return
    }
    if ([string]::IsNullOrWhiteSpace($SignCertificatePath)) {
        throw "-Sign requires -SignCertificatePath."
    }
    if (-not (Test-Path -LiteralPath $SignCertificatePath)) {
        throw "Signing certificate was not found: $SignCertificatePath"
    }

    $Password = [Environment]::GetEnvironmentVariable($SignCertificatePasswordEnv)
    if ([string]::IsNullOrEmpty($Password)) {
        throw "Signing password environment variable is empty: $SignCertificatePasswordEnv"
    }

    $SignTool = Resolve-SignTool
    Write-Host ">$SignTool sign /fd SHA256 /td SHA256 /tr $TimestampUrl /f $SignCertificatePath /p **** $Path"
    $CommandOutput = & $SignTool sign /fd SHA256 /td SHA256 /tr $TimestampUrl /f $SignCertificatePath /p $Password $Path 2>&1
    $ExitCode = $LASTEXITCODE
    foreach ($Line in $CommandOutput) {
        Write-Host $Line
    }
    if ($ExitCode -ne 0) {
        throw "signtool failed with exit code $ExitCode for $Path"
    }
}

function Add-ChecksumLine {
    param(
        [string]$ChecksumsPath,
        [string]$FilePath,
        [string]$DistRoot
    )

    $Relative = [System.IO.Path]::GetRelativePath($DistRoot, $FilePath).Replace("\", "/")
    $Hash = Get-FileSha256 -Path $FilePath
    Add-Content -LiteralPath $ChecksumsPath -Encoding ASCII -Value "$Hash  $Relative"
    return $Hash
}

function New-VersionJson {
    param(
        [string]$BundleDir,
        [string]$Runtime,
        [string]$Deployment,
        [bool]$SelfContained,
        [string]$GuiExe,
        [string]$CoreExe
    )

    $Document = [ordered]@{
        schema = 1
        product = "x-tunnel-client"
        version = $ReleaseVersion
        package_version = $PackageVersion
        runtime = $Runtime
        deployment = $Deployment
        self_contained = $SelfContained
        built_at = $BuildDate
        source_repository = $ClientRemote
        commit = $ClientCommit
        core = [ordered]@{
            repository = $CoreRemote
            commit = $CoreCommit
            executable = "core/x-tunnel.exe"
            sha256 = Get-FileSha256 -Path $CoreExe
        }
        gui = [ordered]@{
            executable = "XTunnelClient.App.exe"
            sha256 = Get-FileSha256 -Path $GuiExe
        }
    }

    $Document | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $BundleDir "version.json") -Encoding UTF8
}

function New-PortablePackage {
    param(
        [string]$Runtime,
        [string]$Deployment,
        [bool]$SelfContained,
        [string]$CoreExe
    )

    $PublishDir = Join-Path $Intermediate "publish\$Runtime\$Deployment"
    $BundleName = "x-tunnel-client_$AssetVersion`_$Runtime`_$Deployment"
    $BundleDir = Join-Path $Intermediate "bundle\$BundleName"
    $ZipPath = Join-Path $Dist "$BundleName.zip"

    $PublishArgs = @(
        "publish",
        (Join-Path $Repo "src\XTunnelClient.App\XTunnelClient.App.csproj"),
        "-c", $Configuration,
        "-r", $Runtime,
        "-o", $PublishDir,
        "-p:Version=$PackageVersion",
        "-p:InformationalVersion=$InformationalVersion",
        "-p:ContinuousIntegrationBuild=true",
        "-p:PublishTrimmed=false"
    )

    if ($SelfContained) {
        $PublishArgs += @(
            "--self-contained", "true",
            "-p:PublishSingleFile=true",
            "-p:IncludeNativeLibrariesForSelfExtract=true",
            "-p:EnableCompressionInSingleFile=true"
        )
    }
    else {
        $PublishArgs += @(
            "--self-contained", "false",
            "-p:PublishSingleFile=false"
        )
    }

    Invoke-Tool -File $DotnetCli -Arguments $PublishArgs
    Copy-PublishToBundle -PublishDir $PublishDir -BundleDir $BundleDir -CoreExe $CoreExe

    $GuiExe = Join-Path $BundleDir "XTunnelClient.App.exe"
    $BundledCoreExe = Join-Path $BundleDir "core\x-tunnel.exe"
    if (-not (Test-Path -LiteralPath $GuiExe)) {
        throw "Published GUI executable was not found: $GuiExe"
    }
    if (-not (Test-Path -LiteralPath $BundledCoreExe)) {
        throw "Bundled core executable was not found: $BundledCoreExe"
    }

    Invoke-CodeSign -Path $GuiExe
    Invoke-CodeSign -Path $BundledCoreExe

    New-VersionJson -BundleDir $BundleDir -Runtime $Runtime -Deployment $Deployment -SelfContained $SelfContained -GuiExe $GuiExe -CoreExe $BundledCoreExe

    if (Test-Path -LiteralPath $ZipPath) {
        Remove-Item -LiteralPath $ZipPath -Force
    }
    Compress-Archive -Path (Join-Path $BundleDir "*") -DestinationPath $ZipPath -CompressionLevel Optimal

    $Hash = Add-ChecksumLine -ChecksumsPath $ChecksumsPath -FilePath $ZipPath -DistRoot $Dist
    $Script:Assets.Add([ordered]@{
        name = [System.IO.Path]::GetFileName($ZipPath)
        kind = "portable"
        runtime = $Runtime
        deployment = $Deployment
        self_contained = $SelfContained
        path = [System.IO.Path]::GetRelativePath($Dist, $ZipPath).Replace("\", "/")
        size = (Get-Item -LiteralPath $ZipPath).Length
        sha256 = $Hash
        gui_sha256 = Get-FileSha256 -Path $GuiExe
        core_sha256 = Get-FileSha256 -Path $BundledCoreExe
    })

    return [pscustomobject]@{
        bundle = $BundleDir
        zip = $ZipPath
    }
}

function New-MsiPackage {
    param(
        [string]$Runtime,
        [string]$PayloadDir
    )

    if ($SkipInstaller) {
        return
    }

    $MsiPath = Join-Path $Dist "x-tunnel-client_$AssetVersion`_$Runtime`_setup.msi"
    $IconPath = Join-Path $Repo "src\XTunnelClient.App\Assets\app-icon.ico"
    $WixArch = Get-WixArch -Runtime $Runtime

    Invoke-Tool -File $DotnetCli -Arguments @("tool", "restore")
    Invoke-Tool -File $DotnetCli -Arguments @(
        "tool", "run", "wix", "build",
        (Join-Path $Repo "installer\Product.wxs"),
        "-arch", $WixArch,
        "-o", $MsiPath,
        "-pdbtype", "none",
        "-d", "ProductName=x-tunnel Client",
        "-d", "Manufacturer=XTunnel",
        "-d", "ProductVersion=$MsiVersion",
        "-d", "DisplayVersion=$PackageVersion",
        "-d", "PayloadDir=$PayloadDir",
        "-d", "IconPath=$IconPath",
        "-d", "UpgradeCode=E7394B2D-B4EF-4B95-A8E4-3A35872A7D51"
    )

    Invoke-CodeSign -Path $MsiPath

    $WixPdb = [System.IO.Path]::ChangeExtension($MsiPath, ".wixpdb")
    if (Test-Path -LiteralPath $WixPdb) {
        Remove-Item -LiteralPath $WixPdb -Force
    }

    if (-not $SkipMsiValidation) {
        Invoke-Tool -File $DotnetCli -Arguments @("tool", "run", "wix", "msi", "validate", $MsiPath)
    }

    $Hash = Add-ChecksumLine -ChecksumsPath $ChecksumsPath -FilePath $MsiPath -DistRoot $Dist
    $Script:Assets.Add([ordered]@{
        name = [System.IO.Path]::GetFileName($MsiPath)
        kind = "installer"
        runtime = $Runtime
        deployment = "self-contained"
        self_contained = $true
        path = [System.IO.Path]::GetRelativePath($Dist, $MsiPath).Replace("\", "/")
        size = (Get-Item -LiteralPath $MsiPath).Length
        sha256 = $Hash
    })
}

$DotnetCli = Resolve-DotnetCli -Requested $Dotnet
$ReleaseVersion = Get-ReleaseVersion
$PackageVersion = Convert-ToPackageVersion -RawVersion $ReleaseVersion
$MsiVersion = Convert-ToMsiVersion -PackageVersion $PackageVersion
$AssetVersion = ($ReleaseVersion -replace '^v', '') -replace '[^0-9A-Za-z._-]+', '-'
$BuildDate = (Get-Date).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ")
$ClientCommit = Get-GitOutput -WorkingDirectory $Repo -Arguments @("rev-parse", "HEAD")
$ClientCommitShort = Get-GitOutput -WorkingDirectory $Repo -Arguments @("rev-parse", "--short=12", "HEAD")
$ClientRemote = Get-GitOutput -WorkingDirectory $Repo -Arguments @("config", "--get", "remote.origin.url")
$CoreRepoFull = (Resolve-Path -LiteralPath $CoreRepo).Path
$CoreCommit = Get-GitOutput -WorkingDirectory $CoreRepoFull -Arguments @("rev-parse", "HEAD")
$CoreCommitShort = Get-GitOutput -WorkingDirectory $CoreRepoFull -Arguments @("rev-parse", "--short=12", "HEAD")
$CoreRemote = Get-GitOutput -WorkingDirectory $CoreRepoFull -Arguments @("config", "--get", "remote.origin.url")
$InformationalVersion = "$PackageVersion+$ClientCommitShort"
$Dist = [System.IO.Path]::GetFullPath($Output)
$Intermediate = Join-Path ([System.IO.Path]::GetFullPath((Join-Path $Repo "artifacts"))) "obj\release"
$ChecksumsPath = Join-Path $Dist "SHA256SUMS"
$ManifestPath = Join-Path $Dist "release-manifest.json"
$Assets = [System.Collections.Generic.List[object]]::new()

Assert-SafeOutputPath -Path $Dist
if ((Test-Path -LiteralPath $Dist) -and -not $NoClean) {
    Remove-Item -LiteralPath $Dist -Recurse -Force
}
New-Item -ItemType Directory -Force -Path $Dist | Out-Null
New-CleanDirectory -Path $Intermediate
Set-Content -LiteralPath $ChecksumsPath -Encoding ASCII -Value ""

Write-Host "Release version: $ReleaseVersion"
Write-Host "Package version: $PackageVersion"
Write-Host "MSI version: $MsiVersion"
Write-Host "Client commit: $ClientCommit"
Write-Host "Core commit: $CoreCommit"

Invoke-Tool -File $DotnetCli -Arguments @("tool", "restore")
$DotnetSdkVersion = (& $DotnetCli --version).Trim()
$GoVersionText = (& go version).Trim()
$WixVersionText = (& $DotnetCli tool run wix --version).Trim()

if ($DotnetSdkVersion -ne [string]$ReleaseConfig.dotnetSdk) {
    Write-Warning "Configured .NET SDK is $($ReleaseConfig.dotnetSdk), but the active SDK is $DotnetSdkVersion."
}
if ($GoVersionText -notmatch "go$([regex]::Escape([string]$ReleaseConfig.goVersion))(\s|$)") {
    Write-Warning "Configured Go version is $($ReleaseConfig.goVersion), but the active Go tool is '$GoVersionText'."
}
if (-not $WixVersionText.StartsWith([string]$ReleaseConfig.wixVersion, [StringComparison]::OrdinalIgnoreCase)) {
    Write-Warning "Configured WiX version is $($ReleaseConfig.wixVersion), but the active WiX tool is '$WixVersionText'."
}

if (-not $SkipTests) {
    Invoke-Tool -File $DotnetCli -Arguments @("restore", (Join-Path $Repo "XTunnelClient.sln"))
    Invoke-Tool -File $DotnetCli -Arguments @("test", (Join-Path $Repo "XTunnelClient.sln"), "-c", $Configuration, "--no-restore")
    Invoke-Tool -File "go" -Arguments @("test", "./...") -WorkingDirectory $CoreRepoFull
}

$CoreBuilds = @{}
foreach ($Runtime in $Runtimes) {
    $GoArch = Get-GoArch -Runtime $Runtime
    $CoreOut = Join-Path $Intermediate "core\$Runtime\x-tunnel.exe"
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $CoreOut) | Out-Null

    $PreviousGoos = $env:GOOS
    $PreviousGoarch = $env:GOARCH
    $PreviousCgo = $env:CGO_ENABLED
    try {
        $env:GOOS = "windows"
        $env:GOARCH = $GoArch
        $env:CGO_ENABLED = "0"
        Invoke-Tool -File "go" -Arguments @(
            "build",
            "-trimpath",
            "-ldflags", "-s -w -X main.buildVersion=$ReleaseVersion -X main.buildCommit=$CoreCommitShort -X main.buildDate=$BuildDate",
            "-o", $CoreOut,
            ".\cmd\x-tunnel"
        ) -WorkingDirectory $CoreRepoFull
    }
    finally {
        $env:GOOS = $PreviousGoos
        $env:GOARCH = $PreviousGoarch
        $env:CGO_ENABLED = $PreviousCgo
    }

    $CoreBuilds[$Runtime] = $CoreOut
}

foreach ($Runtime in $Runtimes) {
    if (-not $SkipFrameworkDependent) {
        New-PortablePackage -Runtime $Runtime -Deployment "framework-dependent" -SelfContained $false -CoreExe $CoreBuilds[$Runtime] | Out-Null
    }

    $SelfContainedPackage = $null
    if (-not $SkipSelfContained) {
        $SelfContainedPackage = New-PortablePackage -Runtime $Runtime -Deployment "self-contained" -SelfContained $true -CoreExe $CoreBuilds[$Runtime]
    }

    if (-not $SkipInstaller) {
        if ($null -eq $SelfContainedPackage) {
            throw "MSI packaging requires a self-contained payload. Remove -SkipSelfContained or add -SkipInstaller."
        }
        New-MsiPackage -Runtime $Runtime -PayloadDir $SelfContainedPackage.bundle
    }
}

$Manifest = [ordered]@{
    schema = 1
    product = "x-tunnel-client"
    version = $ReleaseVersion
    package_version = $PackageVersion
    msi_version = $MsiVersion
    built_at = $BuildDate
    configuration = $Configuration
    runtimes = $Runtimes
    source_repository = $ClientRemote
    commit = $ClientCommit
    dotnet_sdk = $DotnetSdkVersion
    configured_dotnet_sdk = $ReleaseConfig.dotnetSdk
    go_version = $GoVersionText
    configured_go_version = $ReleaseConfig.goVersion
    wix_version = $WixVersionText
    configured_wix_version = $ReleaseConfig.wixVersion
    core = [ordered]@{
        repository = $CoreRemote
        commit = $CoreCommit
        configured_ref = $ReleaseConfig.coreRef
    }
    assets = $Assets
}

$Manifest | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $ManifestPath -Encoding UTF8
$ManifestHash = Add-ChecksumLine -ChecksumsPath $ChecksumsPath -FilePath $ManifestPath -DistRoot $Dist

Write-Host "Release manifest: $ManifestPath"
Write-Host "Release manifest SHA256: $ManifestHash"
Write-Host "Artifacts written to: $Dist"
