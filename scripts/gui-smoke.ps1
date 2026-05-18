param(
    [string]$AppExe = (Join-Path $PSScriptRoot "..\src\XTunnelClient.App\bin\Debug\net8.0-windows\XTunnelClient.App.exe"),
    [string]$CoreRepo = (Join-Path $PSScriptRoot "..\..\x-tunnel"),
    [string]$CoreExe = (Join-Path $PSScriptRoot "..\..\x-tunnel\build\x-tunnel.exe"),
    [string]$AppHome = (Join-Path $env:TEMP ("xtunnel-client-gui-" + [Guid]::NewGuid().ToString("N"))),
    [string]$InstanceName = ("Local\x-tunnel-client-gui-" + [Guid]::NewGuid().ToString("N")),
    [string]$OverviewScreenshotPath = (Join-Path $PSScriptRoot "..\artifacts\gui-smoke-overview.png"),
    [string]$OverviewNetworkScreenshotPath = (Join-Path $PSScriptRoot "..\artifacts\gui-smoke-overview-network.png"),
    [string]$OverviewRuntimeScreenshotPath = (Join-Path $PSScriptRoot "..\artifacts\gui-smoke-overview-runtime.png"),
    [string]$ScreenshotPath = (Join-Path $PSScriptRoot "..\artifacts\gui-smoke.png"),
    [string]$ProfileNarrowScreenshotPath = (Join-Path $PSScriptRoot "..\artifacts\gui-smoke-profiles-narrow.png"),
    [string]$SubscriptionScreenshotPath = (Join-Path $PSScriptRoot "..\artifacts\gui-smoke-subscriptions.png"),
    [string]$DiagnosticsScreenshotPath = (Join-Path $PSScriptRoot "..\artifacts\gui-smoke-diagnostics.png"),
    [string]$DiagnosticsNarrowScreenshotPath = (Join-Path $PSScriptRoot "..\artifacts\gui-smoke-diagnostics-narrow.png"),
    [string]$LogsScreenshotPath = (Join-Path $PSScriptRoot "..\artifacts\gui-smoke-logs.png"),
    [string]$LogsRegexScreenshotPath = (Join-Path $PSScriptRoot "..\artifacts\gui-smoke-logs-regex.png"),
    [string]$LogsInvalidRegexScreenshotPath = (Join-Path $PSScriptRoot "..\artifacts\gui-smoke-logs-invalid-regex.png"),
    [string]$SettingsScreenshotPath = (Join-Path $PSScriptRoot "..\artifacts\gui-smoke-settings.png"),
    [int]$TimeoutSeconds = 25
)

$ErrorActionPreference = "Stop"

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.IO.Compression.FileSystem
Add-Type @"
using System;
using System.Runtime.InteropServices;

public static class NativeWindowMethods
{
    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool MoveWindow(IntPtr hWnd, int X, int Y, int nWidth, int nHeight, bool bRepaint);
}
"@

function Get-FreeTcpPort {
    $listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, 0)
    $listener.Start()
    try {
        return ([System.Net.IPEndPoint]$listener.LocalEndpoint).Port
    }
    finally {
        $listener.Stop()
    }
}

function Wait-Tcp {
    param(
        [string]$HostName,
        [int]$Port,
        [int]$TimeoutSeconds
    )
    Wait-Until -TimeoutSeconds $TimeoutSeconds -Message "Timed out waiting for TCP $HostName`:$Port." -Condition {
        $client = [System.Net.Sockets.TcpClient]::new()
        try {
            $task = $client.ConnectAsync($HostName, $Port)
            if (!$task.Wait(250)) {
                return $null
            }
            return $client.Connected
        }
        catch {
            return $null
        }
        finally {
            $client.Dispose()
        }
    } | Out-Null
}

function Send-DummyHttpRequest {
    param(
        [string]$HostName,
        [int]$Port
    )
    $client = [System.Net.Sockets.TcpClient]::new()
    try {
        $task = $client.ConnectAsync($HostName, $Port)
        if (!$task.Wait(250) -or !$client.Connected) {
            return
        }
        $request = [System.Text.Encoding]::ASCII.GetBytes("GET /__shutdown HTTP/1.1`r`nHost: $HostName`r`nConnection: close`r`n`r`n")
        $stream = $client.GetStream()
        $stream.Write($request, 0, $request.Length)
    }
    catch {
    }
    finally {
        $client.Dispose()
    }
}

function Stop-ChildPowerShellJobs {
    param([int]$ParentProcessId)
    try {
        Get-CimInstance Win32_Process |
            Where-Object {
                $_.ParentProcessId -eq $ParentProcessId -and
                $_.Name -in @("powershell.exe", "pwsh.exe") -and
                $_.CommandLine -match " -s "
            } |
            ForEach-Object {
                Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue
            }
    }
    catch {
    }
}

function Stop-ListenerJob {
    param(
        [object]$Job,
        [int]$Port,
        [int]$RequestCount
    )
    if (!$Job) {
        return
    }
    if ($Job.State -eq "Running") {
        for ($i = 0; $i -lt $RequestCount; $i++) {
            Send-DummyHttpRequest -HostName "127.0.0.1" -Port $Port
        }
        Wait-Job -Job $Job -Timeout 2 -ErrorAction SilentlyContinue | Out-Null
    }
    if ($Job.State -eq "Running") {
        Stop-ChildPowerShellJobs -ParentProcessId $PID
        Wait-Job -Job $Job -Timeout 2 -ErrorAction SilentlyContinue | Out-Null
    }
    Remove-Job $Job -Force -ErrorAction SilentlyContinue
}

function Set-SmokeClipboardText {
    param([AllowNull()][AllowEmptyString()][string]$Value)
    $text = $Value
    if ([string]::IsNullOrEmpty($Value)) {
        $text = " "
    }
    $last = $null
    for ($i = 0; $i -lt 10; $i++) {
        try {
            Set-Clipboard -Value $text
            Start-Sleep -Milliseconds 150
            return
        }
        catch {
            $last = $_
            Start-Sleep -Milliseconds 100
        }
    }
    throw $last
}

function Clear-SmokeClipboard {
    Set-SmokeClipboardText -Value "__xtunnel_client_smoke_clipboard_marker__"
}

function Wait-Until {
    param(
        [scriptblock]$Condition,
        [int]$TimeoutSeconds,
        [string]$Message
    )
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds)
    $last = $null
    while ([DateTimeOffset]::UtcNow -lt $deadline) {
        try {
            $value = & $Condition
            if ($value) {
                return $value
            }
        }
        catch {
            $last = $_
        }
        Start-Sleep -Milliseconds 250
    }
    if ($last) {
        throw "$Message Last error: $($last.Exception.Message)"
    }
    throw $Message
}

function Set-Utf8NoBomContent {
    param(
        [string]$Path,
        [string]$Value
    )
    # Windows PowerShell 5 writes a BOM for -Encoding UTF8, and the Go core
    # rejects a BOM-prefixed JSON config.
    $encoding = New-Object System.Text.UTF8Encoding -ArgumentList $false
    [System.IO.File]::WriteAllText([System.IO.Path]::GetFullPath($Path), $Value, $encoding)
}

function Find-ByAutomationId {
    param(
        [System.Windows.Automation.AutomationElement]$Root,
        [string]$AutomationId
    )
    $condition = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::AutomationIdProperty,
        $AutomationId)
    return $Root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
}

function Find-AllByAutomationId {
    param(
        [System.Windows.Automation.AutomationElement]$Root,
        [string]$AutomationId
    )
    $condition = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::AutomationIdProperty,
        $AutomationId)
    return $Root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $condition)
}

function Get-ByAutomationId {
    param(
        [System.Windows.Automation.AutomationElement]$Root,
        [string]$AutomationId,
        [int]$TimeoutSeconds
    )
    return Wait-Until -TimeoutSeconds $TimeoutSeconds -Message "Timed out waiting for automation id '$AutomationId'." -Condition {
        Find-ByAutomationId -Root $Root -AutomationId $AutomationId
    }
}

function Invoke-Element {
    param([System.Windows.Automation.AutomationElement]$Element)
    $id = $Element.Current.AutomationId
    $name = $Element.Current.Name
    Write-Host "Invoke: $id $name"
    try {
        $pattern = $Element.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
        $pattern.Invoke()
    }
    catch {
        throw "Failed to invoke '$id' '$name': $($_.Exception.Message)"
    }
}

function Select-Element {
    param([System.Windows.Automation.AutomationElement]$Element)
    $pattern = $Element.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)
    $pattern.Select()
}

function Select-ComboBoxItem {
    param(
        [System.Windows.Automation.AutomationElement]$Element,
        [string]$Name,
        [int]$TimeoutSeconds
    )
    $expand = $Element.GetCurrentPattern([System.Windows.Automation.ExpandCollapsePattern]::Pattern)
    $expand.Expand()
    try {
        $item = Wait-Until -TimeoutSeconds $TimeoutSeconds -Message "Timed out waiting for combo box item '$Name'." -Condition {
            $condition = [System.Windows.Automation.PropertyCondition]::new(
                [System.Windows.Automation.AutomationElement]::NameProperty,
                $Name)
            [System.Windows.Automation.AutomationElement]::RootElement.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
        }
        Select-Element $item
    }
    finally {
        try {
            $expand.Collapse()
        }
        catch {
        }
    }
}

function Set-ElementValue {
    param(
        [System.Windows.Automation.AutomationElement]$Element,
        [string]$Value
    )
    $pattern = $Element.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
    $pattern.SetValue($Value)
}

function Get-ElementValue {
    param([System.Windows.Automation.AutomationElement]$Element)
    try {
        $pattern = $Element.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
        return $pattern.Current.Value
    }
    catch {
        return $Element.Current.Name
    }
}

function Wait-AutomationTextMatch {
    param(
        [System.Windows.Automation.AutomationElement]$Root,
        [string]$AutomationId,
        [string]$Pattern,
        [int]$TimeoutSeconds,
        [string]$Message
    )
    $state = @{
        Seen = $false
        LastText = $null
    }
    try {
        return Wait-Until -TimeoutSeconds $TimeoutSeconds -Message $Message -Condition {
            $element = Find-ByAutomationId -Root $Root -AutomationId $AutomationId
            if ($element) {
                $state.Seen = $true
                $state.LastText = Get-ElementValue $element
                if ($state.LastText -match $Pattern) {
                    return $state.LastText
                }
            }
            return $null
        }
    }
    catch {
        if ($state.Seen) {
            throw "$Message Last observed ${AutomationId}: '$($state.LastText)'."
        }
        throw "$Message Last observed ${AutomationId}: <not found>."
    }
}

function Get-ToggleState {
    param([System.Windows.Automation.AutomationElement]$Element)
    $pattern = $Element.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
    return $pattern.Current.ToggleState.ToString()
}

function Save-ElementScreenshot {
    param(
        [System.Windows.Automation.AutomationElement]$Element,
        [string]$Path
    )
    $rect = $Element.Current.BoundingRectangle
    if ($rect.Width -le 0 -or $rect.Height -le 0) {
        throw "Cannot capture screenshot for an empty window rectangle."
    }
    $directory = Split-Path -Parent $Path
    if ($directory) {
        New-Item -ItemType Directory -Force -Path $directory | Out-Null
    }
    $bitmap = [System.Drawing.Bitmap]::new([int]$rect.Width, [int]$rect.Height)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    try {
        $graphics.CopyFromScreen([int]$rect.Left, [int]$rect.Top, 0, 0, $bitmap.Size)
        $bitmap.Save($Path, [System.Drawing.Imaging.ImageFormat]::Png)
    }
    finally {
        $graphics.Dispose()
        $bitmap.Dispose()
    }
}

function Get-MainWindowHandle {
    param(
        [System.Diagnostics.Process]$Process,
        [int]$TimeoutSeconds
    )
    return Wait-Until -TimeoutSeconds $TimeoutSeconds -Message "Timed out waiting for a main window handle." -Condition {
        $Process.Refresh()
        if ($Process.MainWindowHandle -ne [IntPtr]::Zero) {
            return $Process.MainWindowHandle
        }
        return $null
    }
}

function Save-WindowScreenshotAtSize {
    param(
        [System.Windows.Automation.AutomationElement]$Element,
        [System.Diagnostics.Process]$Process,
        [string]$Path,
        [int]$Width,
        [int]$Height,
        [int]$TimeoutSeconds
    )
    $handle = Get-MainWindowHandle -Process $Process -TimeoutSeconds $TimeoutSeconds
    $rect = $Element.Current.BoundingRectangle
    if (-not [NativeWindowMethods]::MoveWindow($handle, [int]$rect.Left, [int]$rect.Top, $Width, $Height, $true)) {
        throw "Failed to resize window for screenshot '$Path'."
    }
    Start-Sleep -Milliseconds 500
    try {
        Save-ElementScreenshot -Element $Element -Path $Path
    }
    finally {
        [NativeWindowMethods]::MoveWindow($handle, [int]$rect.Left, [int]$rect.Top, [int]$rect.Width, [int]$rect.Height, $true) | Out-Null
        Start-Sleep -Milliseconds 500
    }
}

if (!(Test-Path $AppExe)) {
    throw "App executable not found: $AppExe"
}
if (!(Test-Path $CoreExe)) {
    if (!(Test-Path $CoreRepo)) {
        throw "Core executable not found and core repo is unavailable: $CoreExe"
    }
    Write-Host "Core executable not found; building $CoreExe"
    Push-Location $CoreRepo
    try {
        go build -o $CoreExe .\cmd\x-tunnel
    }
    finally {
        Pop-Location
    }
    if (!(Test-Path $CoreExe)) {
        throw "Core executable was not created: $CoreExe"
    }
}

$port = Get-FreeTcpPort
$subscriptionPort = Get-FreeTcpPort
$coreServerPort = Get-FreeTcpPort
$socksPort = Get-FreeTcpPort
$httpPort = Get-FreeTcpPort
$testUrl = "http://127.0.0.1:$port/generate_204"
$coreForwardUrl = "ws://127.0.0.1:$coreServerPort/tunnel"
$profileListen = "socks5://127.0.0.1:$socksPort,http://127.0.0.1:$httpPort"
$subscriptionUrl = "http://127.0.0.1:$subscriptionPort/subscription.json"
$serverRequestCount = 6
$subscriptionRequestCount = 2
$serverJob = Start-Job -ScriptBlock {
    param([int]$Port, [int]$RequestCount)
    $listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, $Port)
    $listener.Start()
    try {
        for ($i = 0; $i -lt $RequestCount; $i++) {
            $client = $listener.AcceptTcpClient()
            try {
                $stream = $client.GetStream()
                $buffer = New-Object byte[] 1024
                $null = $stream.Read($buffer, 0, $buffer.Length)
                $response = [System.Text.Encoding]::ASCII.GetBytes("HTTP/1.1 204 No Content`r`nContent-Length: 0`r`nConnection: close`r`n`r`n")
                $stream.Write($response, 0, $response.Length)
            }
            finally {
                $client.Dispose()
            }
        }
    }
    finally {
        $listener.Stop()
    }
} -ArgumentList $port, $serverRequestCount

$subscriptionJob = Start-Job -ScriptBlock {
    param([int]$Port, [int]$RequestCount)
    $body = '[{"name":"Smoke subscription profile","core_config":{"listen":"socks5://127.0.0.1:12080","forward":"ws://127.0.0.1:18080/tunnel","token_ref":"secret:profile-token","connections":1}}]'
    $listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, $Port)
    $listener.Start()
    try {
        for ($i = 0; $i -lt $RequestCount; $i++) {
            $client = $listener.AcceptTcpClient()
            try {
                $stream = $client.GetStream()
                $buffer = New-Object byte[] 2048
                $null = $stream.Read($buffer, 0, $buffer.Length)
                $bodyBytes = [System.Text.Encoding]::UTF8.GetBytes($body)
                $header = "HTTP/1.1 200 OK`r`nContent-Type: application/json`r`nContent-Length: $($bodyBytes.Length)`r`nConnection: close`r`n`r`n"
                $headerBytes = [System.Text.Encoding]::ASCII.GetBytes($header)
                $stream.Write($headerBytes, 0, $headerBytes.Length)
                $stream.Write($bodyBytes, 0, $bodyBytes.Length)
            }
            finally {
                $client.Dispose()
            }
        }
    }
    finally {
        $listener.Stop()
    }
} -ArgumentList $subscriptionPort, $subscriptionRequestCount

$oldHome = $env:XTUNNEL_CLIENT_HOME
$oldInstance = $env:XTUNNEL_CLIENT_INSTANCE
$oldClipboard = $null
try {
    $oldClipboard = Get-Clipboard -Raw -ErrorAction SilentlyContinue
}
catch {
}
$process = $null
$serverProcess = $null
try {
    New-Item -ItemType Directory -Force -Path $AppHome | Out-Null
    $serverConfig = Join-Path $AppHome "gui-smoke-server.json"
    $serverConfigJson = @"
{
  "listen": "ws://127.0.0.1:$coreServerPort/tunnel",
  "token": "smoke-token",
  "cidr": "127.0.0.1/32",
  "allow-target": "127.0.0.0/8",
  "fallback": true,
  "shutdown_timeout": "2s"
}
"@
    Set-Utf8NoBomContent -Path $serverConfig -Value $serverConfigJson
    $serverProcess = Start-Process -FilePath (Resolve-Path $CoreExe).Path -ArgumentList "-config `"$serverConfig`"" -PassThru -WindowStyle Hidden
    Wait-Tcp -HostName "127.0.0.1" -Port $coreServerPort -TimeoutSeconds $TimeoutSeconds

    $env:XTUNNEL_CLIENT_HOME = $AppHome
    $env:XTUNNEL_CLIENT_INSTANCE = $InstanceName
    $process = Start-Process -FilePath (Resolve-Path $AppExe).Path -PassThru
    $env:XTUNNEL_CLIENT_HOME = $oldHome
    $env:XTUNNEL_CLIENT_INSTANCE = $oldInstance

    $root = [System.Windows.Automation.AutomationElement]::RootElement
    $window = Wait-Until -TimeoutSeconds $TimeoutSeconds -Message "Timed out waiting for x-tunnel Client window." -Condition {
        $condition = [System.Windows.Automation.PropertyCondition]::new(
            [System.Windows.Automation.AutomationElement]::ProcessIdProperty,
            $process.Id)
        $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $condition)
    }

    $statusState = Get-ByAutomationId -Root $window -AutomationId "StatusBarStateText" -TimeoutSeconds $TimeoutSeconds
    $statusProfile = Get-ByAutomationId -Root $window -AutomationId "StatusBarProfileText" -TimeoutSeconds $TimeoutSeconds
    $statusProxyMode = Get-ByAutomationId -Root $window -AutomationId "StatusBarProxyModeText" -TimeoutSeconds $TimeoutSeconds
    $statusLocalProxy = Get-ByAutomationId -Root $window -AutomationId "StatusBarLocalProxyText" -TimeoutSeconds $TimeoutSeconds
    $statusTraffic = Get-ByAutomationId -Root $window -AutomationId "StatusBarTrafficText" -TimeoutSeconds $TimeoutSeconds
    $statusChannels = Get-ByAutomationId -Root $window -AutomationId "StatusBarChannelsText" -TimeoutSeconds $TimeoutSeconds
    $statusCore = Get-ByAutomationId -Root $window -AutomationId "StatusBarCoreText" -TimeoutSeconds $TimeoutSeconds
    $statusIssue = Get-ByAutomationId -Root $window -AutomationId "StatusBarIssueText" -TimeoutSeconds $TimeoutSeconds
    $appErrorText = Get-ByAutomationId -Root $window -AutomationId "AppErrorTextBlock" -TimeoutSeconds $TimeoutSeconds
    $stateText = Get-ElementValue $statusState
    $profileText = Get-ElementValue $statusProfile
    if ($stateText -notmatch "Disconnected" -or $profileText -notmatch "Local x-tunnel") {
        throw "Unexpected status bar text: state='$stateText' profile='$profileText'"
    }
    $statusProxyModeText = Get-ElementValue $statusProxyMode
    $statusLocalProxyText = Get-ElementValue $statusLocalProxy
    $statusTrafficText = Get-ElementValue $statusTraffic
    $statusChannelsText = Get-ElementValue $statusChannels
    $statusCoreText = Get-ElementValue $statusCore
    $statusIssueText = Get-ElementValue $statusIssue
    if ($statusProxyModeText -notmatch "Off" -or $statusLocalProxyText -notmatch "HTTP 127.0.0.1:" -or $statusCoreText -notmatch "Core not running") {
        throw "Unexpected status bar details: proxy='$statusProxyModeText' local='$statusLocalProxyText' core='$statusCoreText' issue='$statusIssueText'"
    }
    if ($statusTrafficText -notmatch "0 B up / 0 B down" -or $statusChannelsText -notmatch "0/0 up") {
        throw "Unexpected status bar runtime summaries: traffic='$statusTrafficText' channels='$statusChannelsText'"
    }
    Write-Host "Status bar details: $statusProxyModeText / $statusLocalProxyText / $statusTrafficText / $statusChannelsText / $statusCoreText / $statusIssueText"

    $overviewProxyText = Get-ByAutomationId -Root $window -AutomationId "OverviewProxySummaryText" -TimeoutSeconds $TimeoutSeconds
    $setSystemProxyModeButton = Get-ByAutomationId -Root $window -AutomationId "SetProxyModeSystemButton" -TimeoutSeconds $TimeoutSeconds
    Invoke-Element $setSystemProxyModeButton
    Wait-Until -TimeoutSeconds $TimeoutSeconds -Message "Proxy mode quick action did not switch to System." -Condition {
        $text = Get-ElementValue $overviewProxyText
        if ($text -match "System") {
            return $text
        }
        return $null
    } | Out-Null
    $setPacProxyModeButton = Get-ByAutomationId -Root $window -AutomationId "SetProxyModePacButton" -TimeoutSeconds $TimeoutSeconds
    Invoke-Element $setPacProxyModeButton
    Wait-Until -TimeoutSeconds $TimeoutSeconds -Message "Proxy mode quick action did not switch to PAC." -Condition {
        $text = Get-ElementValue $overviewProxyText
        if ($text -match "Pac") {
            return $text
        }
        return $null
    } | Out-Null
    $setOffProxyModeButton = Get-ByAutomationId -Root $window -AutomationId "SetProxyModeOffButton" -TimeoutSeconds $TimeoutSeconds
    Invoke-Element $setOffProxyModeButton
    $proxyModeText = Wait-Until -TimeoutSeconds $TimeoutSeconds -Message "Proxy mode quick action did not return to Off." -Condition {
        $text = Get-ElementValue $overviewProxyText
        if ($text -match "Off") {
            return $text
        }
        return $null
    }
    Write-Host "Proxy mode quick actions: $proxyModeText"

    $overviewSaveSettingsButton = Get-ByAutomationId -Root $window -AutomationId "OverviewSaveSettingsButton" -TimeoutSeconds $TimeoutSeconds
    Invoke-Element $overviewSaveSettingsButton
    $overviewSaveText = Wait-Until -TimeoutSeconds $TimeoutSeconds -Message "Overview Save did not report settings saved." -Condition {
        $text = Get-ElementValue $appErrorText
        if ($text -match "Settings saved") {
            return $text
        }
        return $null
    }
    Write-Host "Overview settings saved: $overviewSaveText"

    Save-ElementScreenshot -Element $window -Path $OverviewScreenshotPath
    Write-Host "Overview GUI screenshot: $OverviewScreenshotPath"

    $overviewOpenDiagnosticsButton = Get-ByAutomationId -Root $window -AutomationId "OverviewOpenDiagnosticsButton" -TimeoutSeconds $TimeoutSeconds
    Invoke-Element $overviewOpenDiagnosticsButton
    $overviewDiagnosticsLastRun = Wait-Until -TimeoutSeconds $TimeoutSeconds -Message "Overview Diagnostics shortcut did not refresh diagnostics." -Condition {
        $lastRun = Find-ByAutomationId -Root $window -AutomationId "DiagnosticsLastRunText"
        if (!$lastRun) {
            return $null
        }
        $text = Get-ElementValue $lastRun
        if ($text -match "Checks refreshed") {
            return $text
        }
        return $null
    }
    Write-Host "Overview diagnostics shortcut: $overviewDiagnosticsLastRun"

    $overviewTab = Get-ByAutomationId -Root $window -AutomationId "OverviewTab" -TimeoutSeconds $TimeoutSeconds
    Select-Element $overviewTab

    $openSubscriptionsButton = Get-ByAutomationId -Root $window -AutomationId "OpenSubscriptionsButton" -TimeoutSeconds $TimeoutSeconds
    Invoke-Element $openSubscriptionsButton
    Get-ByAutomationId -Root $window -AutomationId "SubscriptionSearchTextBox" -TimeoutSeconds $TimeoutSeconds | Out-Null
    Write-Host "Overview subscriptions shortcut opened Subscriptions"
    Select-Element $overviewTab

    $openLogsButton = Get-ByAutomationId -Root $window -AutomationId "OpenLogsButton" -TimeoutSeconds $TimeoutSeconds
    Invoke-Element $openLogsButton
    Get-ByAutomationId -Root $window -AutomationId "LogFilterTextBox" -TimeoutSeconds $TimeoutSeconds | Out-Null
    Write-Host "Overview logs shortcut opened Logs"
    Select-Element $overviewTab

    $openSettingsButton = Get-ByAutomationId -Root $window -AutomationId "OpenSettingsButton" -TimeoutSeconds $TimeoutSeconds
    Invoke-Element $openSettingsButton
    Get-ByAutomationId -Root $window -AutomationId "SettingsCorePathTextBox" -TimeoutSeconds $TimeoutSeconds | Out-Null
    Write-Host "Overview settings shortcut opened Settings"
    Select-Element $overviewTab

    $openProfilesButton = Get-ByAutomationId -Root $window -AutomationId "OpenProfilesButton" -TimeoutSeconds $TimeoutSeconds
    Invoke-Element $openProfilesButton

    $profileListName = Get-ByAutomationId -Root $window -AutomationId "ProfileListItemName" -TimeoutSeconds $TimeoutSeconds
    $profileListText = Get-ElementValue $profileListName
    if ($profileListText -notmatch "Local x-tunnel") {
        throw "Unexpected profile list item text: '$profileListText'"
    }

    $listenBox = Get-ByAutomationId -Root $window -AutomationId "ProfileListenTextBox" -TimeoutSeconds $TimeoutSeconds
    Set-ElementValue -Element $listenBox -Value $profileListen

    $forwardBox = Get-ByAutomationId -Root $window -AutomationId "ProfileForwardTextBox" -TimeoutSeconds $TimeoutSeconds
    Set-ElementValue -Element $forwardBox -Value $coreForwardUrl

    $secretBox = Get-ByAutomationId -Root $window -AutomationId "ProfileSecretTextBox" -TimeoutSeconds $TimeoutSeconds
    Set-ElementValue -Element $secretBox -Value "smoke-token"

    $applyProfileButton = Get-ByAutomationId -Root $window -AutomationId "ApplyProfileFormButton" -TimeoutSeconds $TimeoutSeconds
    Invoke-Element $applyProfileButton

    $saveProfileButton = Get-ByAutomationId -Root $window -AutomationId "SaveProfileButton" -TimeoutSeconds $TimeoutSeconds
    Invoke-Element $saveProfileButton

    $validateProfileButton = Get-ByAutomationId -Root $window -AutomationId "ValidateProfileButton" -TimeoutSeconds $TimeoutSeconds
    Invoke-Element $validateProfileButton
    $profileValidateText = Wait-Until -TimeoutSeconds $TimeoutSeconds -Message "Validate profile did not report a ready or warning state." -Condition {
        $text = Get-ElementValue $appErrorText
        if ($text -match "Profile ready" -or $text -match "Profile validated with warnings") {
            return $text
        }
        return $null
    }
    Write-Host "Profile validation: $profileValidateText"

    $formatProfileButton = Get-ByAutomationId -Root $window -AutomationId "FormatProfileButton" -TimeoutSeconds $TimeoutSeconds
    Invoke-Element $formatProfileButton
    $profileFormatText = Wait-Until -TimeoutSeconds $TimeoutSeconds -Message "Format profile did not report success." -Condition {
        $text = Get-ElementValue $appErrorText
        if ($text -match "Formatted with core" -or $text -match "formatted locally") {
            return $text
        }
        return $null
    }
    Write-Host "Profile format: $profileFormatText"

    $copyProfileSummaryButton = Get-ByAutomationId -Root $window -AutomationId "CopyProfileSummaryButton" -TimeoutSeconds $TimeoutSeconds
    Clear-SmokeClipboard
    Invoke-Element $copyProfileSummaryButton
    $profileSummaryClipboardText = Wait-Until -TimeoutSeconds $TimeoutSeconds -Message "Copy profile summary did not place a redacted summary on the clipboard." -Condition {
        $text = Get-Clipboard -Raw -ErrorAction SilentlyContinue
        $expectedLocalProxy = "Local proxy: HTTP 127.0.0.1:$httpPort / SOCKS 127.0.0.1:$socksPort"
        $expectedForward = $coreForwardUrl.Replace("/tunnel", "")
        if ($text -match "Profile: Local x-tunnel" -and
            $text -match [Regex]::Escape($expectedLocalProxy) -and
            $text -match [Regex]::Escape("Forward: $expectedForward") -and
            $text -match "Config:" -and
            $text -notmatch "smoke-token|profile-token") {
            return $text
        }
        return $null
    }
    Write-Host "Copied profile summary: $($profileSummaryClipboardText.Split([Environment]::NewLine)[0])"

    $copyProfileConfigButton = Get-ByAutomationId -Root $window -AutomationId "CopyProfileConfigButton" -TimeoutSeconds $TimeoutSeconds
    Clear-SmokeClipboard
    Invoke-Element $copyProfileConfigButton
    $profileConfigClipboardText = Wait-Until -TimeoutSeconds $TimeoutSeconds -Message "Copy profile config did not place the core JSON on the clipboard." -Condition {
        $text = Get-Clipboard -Raw -ErrorAction SilentlyContinue
        if ($text -match '"listen"' -and
            $text -match '"forward"' -and
            $text -match [Regex]::Escape($coreForwardUrl) -and
            $text -notmatch "smoke-token") {
            return $text
        }
        return $null
    }
    Write-Host "Copied profile config: $($profileConfigClipboardText.Split([Environment]::NewLine)[0])"

    $startupProfileButton = Get-ByAutomationId -Root $window -AutomationId "UseStartupProfileButton" -TimeoutSeconds $TimeoutSeconds
    Invoke-Element $startupProfileButton
    $startupProfileSummaryBlock = Get-ByAutomationId -Root $window -AutomationId "StartupProfileSummaryTextBlock" -TimeoutSeconds $TimeoutSeconds
    $startupProfileSummary = Wait-Until -TimeoutSeconds $TimeoutSeconds -Message "Startup profile shortcut did not update the profile summary." -Condition {
        $text = Get-ElementValue $startupProfileSummaryBlock
        if ($text -match "Startup: Local x-tunnel") {
            return $text
        }
        return $null
    }
    Write-Host "Startup profile summary: $startupProfileSummary"
    $clearStartupProfileButton = Get-ByAutomationId -Root $window -AutomationId "ClearStartupProfileButton" -TimeoutSeconds $TimeoutSeconds
    Invoke-Element $clearStartupProfileButton
    $clearedStartupProfileSummary = Wait-Until -TimeoutSeconds $TimeoutSeconds -Message "Clear startup profile did not reset the profile summary." -Condition {
        $text = Get-ElementValue $startupProfileSummaryBlock
        if ($text -match "Startup: not set") {
            return $text
        }
        return $null
    }
    Write-Host "Startup profile cleared: $clearedStartupProfileSummary"
    Invoke-Element $startupProfileButton
    $restoredStartupProfileSummary = Wait-Until -TimeoutSeconds $TimeoutSeconds -Message "Startup profile shortcut did not restore after clearing." -Condition {
        $text = Get-ElementValue $startupProfileSummaryBlock
        if ($text -match "Startup: Local x-tunnel") {
            return $text
        }
        return $null
    }
    Write-Host "Startup profile restored: $restoredStartupProfileSummary"

    $profileBatchTestTextBlock = Get-ByAutomationId -Root $window -AutomationId "ProfileBatchTestTextBlock" -TimeoutSeconds $TimeoutSeconds
    $testSelectedProfileButton = Get-ByAutomationId -Root $window -AutomationId "TestSelectedProfileButton" -TimeoutSeconds $TimeoutSeconds
    Invoke-Element $testSelectedProfileButton
    $selectedProfileTestText = Wait-Until -TimeoutSeconds $TimeoutSeconds -Message "Selected profile endpoint test did not report success." -Condition {
        $text = Get-ElementValue $profileBatchTestTextBlock
        if ($text -match "Selected endpoint ok: Local x-tunnel") {
            return $text
        }
        return $null
    }
    $selectedProfileEndpointState = Wait-Until -TimeoutSeconds $TimeoutSeconds -Message "Selected profile endpoint test did not update the list latency badge." -Condition {
        $items = Find-AllByAutomationId -Root $window -AutomationId "ProfileListItemEndpointState"
        foreach ($item in $items) {
            $text = Get-ElementValue $item
            if ($text -match "TCP \d+ms") {
                return $text
            }
        }
        return $null
    }
    Write-Host "Selected profile endpoint: $selectedProfileTestText / $selectedProfileEndpointState"

    $testVisibleProfilesButton = Get-ByAutomationId -Root $window -AutomationId "TestVisibleProfilesButton" -TimeoutSeconds $TimeoutSeconds
    Invoke-Element $testVisibleProfilesButton
    $profileBatchTestText = Wait-Until -TimeoutSeconds $TimeoutSeconds -Message "Visible profile endpoint test did not report success." -Condition {
        $text = Get-ElementValue $profileBatchTestTextBlock
        if ($text -match "Endpoint tests: 1 ok, 0 failed") {
            return $text
        }
        return $null
    }
    $profileEndpointState = Wait-Until -TimeoutSeconds $TimeoutSeconds -Message "Profile list did not display endpoint latency." -Condition {
        $items = Find-AllByAutomationId -Root $window -AutomationId "ProfileListItemEndpointState"
        foreach ($item in $items) {
            $text = Get-ElementValue $item
            if ($text -match "TCP \d+ms") {
                return $text
            }
        }
        return $null
    }
    Write-Host "Profile endpoint batch: $profileBatchTestText / $profileEndpointState"

    $testAndSelectFastestButton = Get-ByAutomationId -Root $window -AutomationId "TestAndSelectFastestProfileButton" -TimeoutSeconds $TimeoutSeconds
    Invoke-Element $testAndSelectFastestButton
    $profileNameBox = Get-ByAutomationId -Root $window -AutomationId "ProfileNameTextBox" -TimeoutSeconds $TimeoutSeconds
    $fastestProfileName = Wait-Until -TimeoutSeconds $TimeoutSeconds -Message "Test + Fastest did not switch to the tested local profile." -Condition {
        $text = Get-ElementValue $profileNameBox
        if ($text -match "Local x-tunnel") {
            return $text
        }
        return $null
    }
    $fastestText = Wait-Until -TimeoutSeconds $TimeoutSeconds -Message "Test + Fastest did not report the selected profile." -Condition {
        $text = Get-ElementValue $profileBatchTestTextBlock
        if ($text -match "Selected fastest: Local x-tunnel") {
            return $text
        }
        return $null
    }
    Write-Host "Test + Fastest selected: $fastestProfileName / $fastestText"

    $headerDiagnosticsButton = Get-ByAutomationId -Root $window -AutomationId "HeaderDiagnosticsButton" -TimeoutSeconds $TimeoutSeconds
    Invoke-Element $headerDiagnosticsButton

    $diagnosticsPortStatusText = Get-ByAutomationId -Root $window -AutomationId "DiagnosticsPortStatusText" -TimeoutSeconds $TimeoutSeconds
    $diagnosticsPortStatus = Wait-Until -TimeoutSeconds $TimeoutSeconds -Message "Diagnostics port status did not show available listen ports." -Condition {
        $text = Get-ElementValue $diagnosticsPortStatusText
        if ($text -match "Ports: available") {
            return $text
        }
        return $null
    }
    $diagnosticsPortDetailText = Get-ByAutomationId -Root $window -AutomationId "DiagnosticsPortDetailText" -TimeoutSeconds $TimeoutSeconds
    $diagnosticsPortDetail = Get-ElementValue $diagnosticsPortDetailText
    if ($diagnosticsPortDetail -notmatch "127.0.0.1") {
        throw "Diagnostics port detail did not include local listen addresses: '$diagnosticsPortDetail'"
    }
    Write-Host "Diagnostics ports: $diagnosticsPortStatus / $diagnosticsPortDetail"
    $copyDiagnosticsPortsButton = Get-ByAutomationId -Root $window -AutomationId "CopyDiagnosticsPortsButton" -TimeoutSeconds $TimeoutSeconds
    Clear-SmokeClipboard
    Invoke-Element $copyDiagnosticsPortsButton
    $clipboardDiagnosticsPorts = Wait-Until -TimeoutSeconds $TimeoutSeconds -Message "Copy diagnostics ports did not place port details on the clipboard." -Condition {
        $text = Get-Clipboard -Raw -ErrorAction SilentlyContinue
        if ($text -match "Ports: available" -and $text -match "127.0.0.1") {
            return $text
        }
        return $null
    }
    Write-Host "Copied diagnostics ports: $($clipboardDiagnosticsPorts.Split([Environment]::NewLine)[0])"

    $targetCombo = Get-ByAutomationId -Root $window -AutomationId "NetworkTestTargetComboBox" -TimeoutSeconds $TimeoutSeconds
    Select-ComboBoxItem -Element $targetCombo -Name "Microsoft NCSI" -TimeoutSeconds $TimeoutSeconds
    $urlBox = Get-ByAutomationId -Root $window -AutomationId "NetworkTestUrlTextBox" -TimeoutSeconds $TimeoutSeconds
    $microsoftPresetUrl = Wait-Until -TimeoutSeconds $TimeoutSeconds -Message "Microsoft network target preset did not update the test URL." -Condition {
        $text = Get-ElementValue $urlBox
        if ($text -match "msftconnecttest.com/connecttest.txt") {
            return $text
        }
        return $null
    }
    Select-ComboBoxItem -Element $targetCombo -Name "Cloudflare Trace" -TimeoutSeconds $TimeoutSeconds
    $cloudflarePresetUrl = Wait-Until -TimeoutSeconds $TimeoutSeconds -Message "Cloudflare network target preset did not update the test URL." -Condition {
        $text = Get-ElementValue $urlBox
        if ($text -match "cloudflare.com/cdn-cgi/trace") {
            return $text
        }
        return $null
    }
    Write-Host "Network target preset URLs: $microsoftPresetUrl / $cloudflarePresetUrl"

    Set-ElementValue -Element $urlBox -Value "not-a-url"
    $diagnosticsNetworkButton = Get-ByAutomationId -Root $window -AutomationId "TestNetworkButton" -TimeoutSeconds $TimeoutSeconds
    Invoke-Element $diagnosticsNetworkButton
    $resultBox = Get-ByAutomationId -Root $window -AutomationId "NetworkTestResultTextBox" -TimeoutSeconds $TimeoutSeconds
    $invalidDirectRouteStatus = Get-ByAutomationId -Root $window -AutomationId "NetworkDirectRouteStatusText" -TimeoutSeconds $TimeoutSeconds
    $invalidProxyRouteStatus = Get-ByAutomationId -Root $window -AutomationId "NetworkProxyRouteStatusText" -TimeoutSeconds $TimeoutSeconds
    $invalidNetworkText = Wait-Until -TimeoutSeconds $TimeoutSeconds -Message "Invalid network target did not report a validation error." -Condition {
        $text = Get-ElementValue $resultBox
        if ($text -match "Target URL must be an absolute http or https URL") {
            return $text
        }
        return $null
    }
    $invalidDirectRouteText = Wait-Until -TimeoutSeconds $TimeoutSeconds -Message "Invalid network target did not mark the direct route chip invalid." -Condition {
        $text = Get-ElementValue $invalidDirectRouteStatus
        if ($text -match "Direct: invalid target") {
            return $text
        }
        return $null
    }
    $invalidProxyRouteText = Wait-Until -TimeoutSeconds $TimeoutSeconds -Message "Invalid network target did not mark the proxy route chip invalid." -Condition {
        $text = Get-ElementValue $invalidProxyRouteStatus
        if ($text -match "Proxy: invalid target") {
            return $text
        }
        return $null
    }
    Write-Host "Invalid network target: $invalidNetworkText / $invalidDirectRouteText / $invalidProxyRouteText"
    Set-ElementValue -Element $urlBox -Value $testUrl

    $overviewTab = Get-ByAutomationId -Root $window -AutomationId "OverviewTab" -TimeoutSeconds $TimeoutSeconds
    Select-Element $overviewTab
    $copyProxyAddressButton = Get-ByAutomationId -Root $window -AutomationId "CopyProxyAddressButton" -TimeoutSeconds $TimeoutSeconds
    Clear-SmokeClipboard
    Invoke-Element $copyProxyAddressButton
    $clipboardProxyAddress = Wait-Until -TimeoutSeconds $TimeoutSeconds -Message "Copy proxy address did not place local HTTP and SOCKS endpoints on the clipboard." -Condition {
        $text = Get-Clipboard -Raw -ErrorAction SilentlyContinue
        if ($text -match "HTTP 127.0.0.1:" -and $text -match "SOCKS 127.0.0.1:") {
            return $text
        }
        return $null
    }
    Write-Host "Copied proxy address: $clipboardProxyAddress"

    $overviewTestNetworkButton = Get-ByAutomationId -Root $window -AutomationId "OverviewTestNetworkButton" -TimeoutSeconds $TimeoutSeconds
    Invoke-Element $overviewTestNetworkButton

    $resultBox = Get-ByAutomationId -Root $window -AutomationId "NetworkTestResultTextBox" -TimeoutSeconds $TimeoutSeconds
    $resultText = Wait-Until -TimeoutSeconds $TimeoutSeconds -Message "Network test did not report success." -Condition {
        $text = Get-ElementValue $resultBox
        if ($text -match "Direct: ok" -and $text -match "status=204") {
            return $text
        }
        return $null
    }
    $directRouteStatus = Get-ByAutomationId -Root $window -AutomationId "NetworkDirectRouteStatusText" -TimeoutSeconds $TimeoutSeconds
    $proxyRouteStatus = Get-ByAutomationId -Root $window -AutomationId "NetworkProxyRouteStatusText" -TimeoutSeconds $TimeoutSeconds
    $directRouteText = Wait-Until -TimeoutSeconds $TimeoutSeconds -Message "Direct route status chip did not show success." -Condition {
        $text = Get-ElementValue $directRouteStatus
        if ($text -match "Direct: ok") {
            return $text
        }
        return $null
    }
    $proxyRouteText = Wait-Until -TimeoutSeconds $TimeoutSeconds -Message "Proxy route status chip did not show the disconnected failure state." -Condition {
        $text = Get-ElementValue $proxyRouteStatus
        if ($text -match "proxy: failed") {
            return $text
        }
        return $null
    }
    Write-Host "Network route chips: $directRouteText / $proxyRouteText"

    Select-Element $overviewTab
    $overviewNetworkSummaryText = Get-ByAutomationId -Root $window -AutomationId "OverviewNetworkSummaryText" -TimeoutSeconds $TimeoutSeconds
    $overviewNetworkText = Wait-Until -TimeoutSeconds $TimeoutSeconds -Message "Overview network summary did not show the network test result." -Condition {
        $text = Get-ElementValue $overviewNetworkSummaryText
        if ($text -match "Direct ok") {
            return $text
        }
        return $null
    }
    $overviewNetworkDetailText = Get-ByAutomationId -Root $window -AutomationId "OverviewNetworkDetailText" -TimeoutSeconds $TimeoutSeconds
    $overviewNetworkLastRunText = Get-ByAutomationId -Root $window -AutomationId "OverviewNetworkLastRunText" -TimeoutSeconds $TimeoutSeconds
    $overviewNetworkLastRun = Wait-Until -TimeoutSeconds $TimeoutSeconds -Message "Overview network summary did not show when the test was last run." -Condition {
        $text = Get-ElementValue $overviewNetworkLastRunText
        if ($text -match "Last tested") {
            return $text
        }
        return $null
    }
    Write-Host "Overview network summary: $overviewNetworkText / $(Get-ElementValue $overviewNetworkDetailText) / $overviewNetworkLastRun"
    $copyOverviewStatusButton = Get-ByAutomationId -Root $window -AutomationId "CopyOverviewStatusButton" -TimeoutSeconds $TimeoutSeconds
    Clear-SmokeClipboard
    Invoke-Element $copyOverviewStatusButton
    $clipboardOverviewStatus = Wait-Until -TimeoutSeconds $TimeoutSeconds -Message "Copy overview status did not place the summary on the clipboard." -Condition {
        $text = Get-Clipboard -Raw -ErrorAction SilentlyContinue
        if ($text -match "Profile: Local x-tunnel" -and $text -match "Traffic: 0 B up / 0 B down" -and $text -match "Channels: 0/0 up" -and $text -match "Network: Direct ok" -and $text -match "Network updated: Last tested" -and $text -match "Local proxy: HTTP") {
            return $text
        }
        return $null
    }
    Write-Host "Copied overview status: $($clipboardOverviewStatus.Split([Environment]::NewLine)[0])"
    Save-ElementScreenshot -Element $window -Path $OverviewNetworkScreenshotPath
    Write-Host "Overview network GUI screenshot: $OverviewNetworkScreenshotPath"

    $diagnosticsTab = Get-ByAutomationId -Root $window -AutomationId "DiagnosticsTab" -TimeoutSeconds $TimeoutSeconds
    Select-Element $diagnosticsTab
    $copyNetworkResultButton = Get-ByAutomationId -Root $window -AutomationId "CopyNetworkTestResultButton" -TimeoutSeconds $TimeoutSeconds
    Clear-SmokeClipboard
    Invoke-Element $copyNetworkResultButton
    $clipboardNetworkText = Wait-Until -TimeoutSeconds $TimeoutSeconds -Message "Copy network result did not place the result on the clipboard." -Condition {
        $text = Get-Clipboard -Raw -ErrorAction SilentlyContinue
        if ($text -match "Direct: ok" -and $text -match "status=204") {
            return $text
        }
        return $null
    }
    Write-Host "Copied network result: $($clipboardNetworkText.Split([Environment]::NewLine)[0])"

    $clearNetworkTestButton = Get-ByAutomationId -Root $window -AutomationId "ClearNetworkTestButton" -TimeoutSeconds $TimeoutSeconds
    Invoke-Element $clearNetworkTestButton
    Wait-Until -TimeoutSeconds $TimeoutSeconds -Message "Clear network test did not reset the Diagnostics result text." -Condition {
        $text = Get-ElementValue $resultBox
        if ($text -match "Not tested") {
            return $text
        }
        return $null
    } | Out-Null
    Wait-Until -TimeoutSeconds $TimeoutSeconds -Message "Clear network test did not reset the direct route chip." -Condition {
        $text = Get-ElementValue $directRouteStatus
        if ($text -match "Direct: not tested") {
            return $text
        }
        return $null
    } | Out-Null
    Select-Element $overviewTab
    Wait-Until -TimeoutSeconds $TimeoutSeconds -Message "Clear network test did not reset the Overview summary." -Condition {
        $text = Get-ElementValue $overviewNetworkSummaryText
        if ($text -match "Not tested") {
            return $text
        }
        return $null
    } | Out-Null
    Wait-Until -TimeoutSeconds $TimeoutSeconds -Message "Clear network test did not reset the Overview last-run text." -Condition {
        $text = Get-ElementValue $overviewNetworkLastRunText
        if ($text -match "Network test not run") {
            return $text
        }
        return $null
    } | Out-Null
    Write-Host "Network test cleared"
    Select-Element $diagnosticsTab

    $endpointButton = Get-ByAutomationId -Root $window -AutomationId "TestProfileEndpointButton" -TimeoutSeconds $TimeoutSeconds
    Invoke-Element $endpointButton

    $endpointResultBox = Get-ByAutomationId -Root $window -AutomationId "ProfileEndpointTestResultTextBox" -TimeoutSeconds $TimeoutSeconds
    $forwardLastRunText = Get-ByAutomationId -Root $window -AutomationId "ProfileEndpointLastRunText" -TimeoutSeconds $TimeoutSeconds
    $endpointText = Wait-Until -TimeoutSeconds $TimeoutSeconds -Message "Profile endpoint test did not report success." -Condition {
        $text = Get-ElementValue $endpointResultBox
        if ($text -match "Forward TCP: ok" -and $text -match [Regex]::Escape($coreForwardUrl.Replace("/tunnel", ""))) {
            return $text
        }
        return $null
    }
    $forwardRouteStatus = Get-ByAutomationId -Root $window -AutomationId "ProfileEndpointRouteStatusText" -TimeoutSeconds $TimeoutSeconds
    $forwardRouteText = Wait-Until -TimeoutSeconds $TimeoutSeconds -Message "Forward route status chip did not show success." -Condition {
        $text = Get-ElementValue $forwardRouteStatus
        if ($text -match "Forward TCP: ok") {
            return $text
        }
        return $null
    }

    $forwardLastRun = Wait-Until -TimeoutSeconds $TimeoutSeconds -Message "Profile endpoint test did not update last-run text." -Condition {
        $text = Get-ElementValue $forwardLastRunText
        if ($text -match "Last tested") {
            return $text
        }
        return $null
    }
    Write-Host $resultText
    Write-Host $endpointText
    Write-Host "Forward route chip: $forwardRouteText / $forwardLastRun"
    $copyEndpointResultButton = Get-ByAutomationId -Root $window -AutomationId "CopyProfileEndpointTestResultButton" -TimeoutSeconds $TimeoutSeconds
    Clear-SmokeClipboard
    Invoke-Element $copyEndpointResultButton
    $clipboardEndpointText = Wait-Until -TimeoutSeconds $TimeoutSeconds -Message "Copy profile endpoint result did not place the result on the clipboard." -Condition {
        $text = Get-Clipboard -Raw -ErrorAction SilentlyContinue
        if ($text -match "Forward TCP: ok" -and $text -match [Regex]::Escape($coreForwardUrl.Replace("/tunnel", ""))) {
            return $text
        }
        return $null
    }
    Write-Host "Copied endpoint result: $($clipboardEndpointText.Split([Environment]::NewLine)[0])"

    $clearEndpointResultButton = Get-ByAutomationId -Root $window -AutomationId "ClearProfileEndpointTestButton" -TimeoutSeconds $TimeoutSeconds
    Invoke-Element $clearEndpointResultButton
    Wait-Until -TimeoutSeconds $TimeoutSeconds -Message "Clear forward result did not reset the result text." -Condition {
        $text = Get-ElementValue $endpointResultBox
        if ($text -match "Not tested") {
            return $text
        }
        return $null
    } | Out-Null
    Wait-Until -TimeoutSeconds $TimeoutSeconds -Message "Clear forward result did not reset the route chip." -Condition {
        $text = Get-ElementValue $forwardRouteStatus
        if ($text -match "Forward TCP: not tested") {
            return $text
        }
        return $null
    } | Out-Null
    Wait-Until -TimeoutSeconds $TimeoutSeconds -Message "Clear forward result did not reset the last-run text." -Condition {
        $text = Get-ElementValue $forwardLastRunText
        if ($text -match "Forward test not run") {
            return $text
        }
        return $null
    } | Out-Null
    Write-Host "Forward result cleared"

    $runAllDiagnosticsButton = Get-ByAutomationId -Root $window -AutomationId "RunAllDiagnosticsButton" -TimeoutSeconds $TimeoutSeconds
    Invoke-Element $runAllDiagnosticsButton
    $runAllNetworkText = Wait-Until -TimeoutSeconds $TimeoutSeconds -Message "Run All did not rerun the network test." -Condition {
        $text = Get-ElementValue $resultBox
        if ($text -match "Direct: ok" -and $text -match "status=204") {
            return $text
        }
        return $null
    }
    $runAllForwardText = Wait-Until -TimeoutSeconds $TimeoutSeconds -Message "Run All did not rerun the forward endpoint test." -Condition {
        $text = Get-ElementValue $endpointResultBox
        if ($text -match "Forward TCP: ok" -and $text -match [Regex]::Escape($coreForwardUrl.Replace("/tunnel", ""))) {
            return $text
        }
        return $null
    }
    $runAllDirectRouteText = Wait-Until -TimeoutSeconds $TimeoutSeconds -Message "Run All did not update the direct route chip." -Condition {
        $text = Get-ElementValue $directRouteStatus
        if ($text -match "Direct: ok") {
            return $text
        }
        return $null
    }
    $runAllForwardRouteText = Wait-Until -TimeoutSeconds $TimeoutSeconds -Message "Run All did not update the forward route chip." -Condition {
        $text = Get-ElementValue $forwardRouteStatus
        if ($text -match "Forward TCP: ok") {
            return $text
        }
        return $null
    }
    $runAllForwardLastRun = Wait-Until -TimeoutSeconds $TimeoutSeconds -Message "Run All did not update the forward last-run text." -Condition {
        $text = Get-ElementValue $forwardLastRunText
        if ($text -match "Last tested") {
            return $text
        }
        return $null
    }
    Write-Host "Run All diagnostics: $($runAllNetworkText.Split([Environment]::NewLine)[0]) / $($runAllForwardText.Split([Environment]::NewLine)[0]) / $runAllDirectRouteText / $runAllForwardRouteText / $runAllForwardLastRun"

    $copyDiagnosticsSummaryButton = Get-ByAutomationId -Root $window -AutomationId "CopyDiagnosticsSummaryButton" -TimeoutSeconds $TimeoutSeconds
    Clear-SmokeClipboard
    Invoke-Element $copyDiagnosticsSummaryButton
    $clipboardDiagnosticsSummary = Wait-Until -TimeoutSeconds $TimeoutSeconds -Message "Copy diagnostics summary did not place the summary on the clipboard." -Condition {
        $text = Get-Clipboard -Raw -ErrorAction SilentlyContinue
        if ($text -match "OS:" -and $text -match "Profile: Local x-tunnel" -and $text -match "Proxy:" -and $text -match "Ports:" -and $text -match "Network: Direct ok" -and $text -match "Forward: Forward TCP: ok") {
            return $text
        }
        return $null
    }
    Write-Host "Copied diagnostics summary: $($clipboardDiagnosticsSummary.Split([Environment]::NewLine)[0])"

    $copyDiagnosticsReportButton = Get-ByAutomationId -Root $window -AutomationId "CopyDiagnosticsReportButton" -TimeoutSeconds $TimeoutSeconds
    Clear-SmokeClipboard
    Invoke-Element $copyDiagnosticsReportButton
    $clipboardDiagnosticsReport = Wait-Until -TimeoutSeconds $TimeoutSeconds -Message "Copy diagnostics report did not place the JSON report on the clipboard." -Condition {
        $text = Get-Clipboard -Raw -ErrorAction SilentlyContinue
        if ($text -match '"createdAt"' -and $text -match '"activeProfile"' -and $text -match '"portChecks"' -and $text -match "Local x-tunnel") {
            return $text
        }
        return $null
    }
    Write-Host "Copied diagnostics report: $($clipboardDiagnosticsReport.Split([Environment]::NewLine)[0])"
    $exportDiagnosticsZipButton = Get-ByAutomationId -Root $window -AutomationId "ExportDiagnosticsZipButton" -TimeoutSeconds $TimeoutSeconds
    Invoke-Element $exportDiagnosticsZipButton
    $exportedDiagnosticsText = Wait-Until -TimeoutSeconds $TimeoutSeconds -Message "Export diagnostics zip did not report an exported file path." -Condition {
        $text = Get-ElementValue $appErrorText
        if ($text -match "Diagnostics exported: .*\.zip") {
            return $text
        }
        return $null
    }
    $exportedDiagnosticsPath = $exportedDiagnosticsText -replace '^Diagnostics exported:\s*', ''
    if (!(Test-Path $exportedDiagnosticsPath)) {
        throw "Exported diagnostics zip was not created: '$exportedDiagnosticsPath'"
    }
    $zip = [System.IO.Compression.ZipFile]::OpenRead($exportedDiagnosticsPath)
    try {
        if (!($zip.Entries | Where-Object { $_.FullName -eq "report.json" })) {
            throw "Exported diagnostics zip did not contain report.json: '$exportedDiagnosticsPath'"
        }
    }
    finally {
        $zip.Dispose()
    }
    Write-Host "Exported diagnostics zip: $exportedDiagnosticsPath"
    Save-ElementScreenshot -Element $window -Path $DiagnosticsScreenshotPath
    Write-Host "Diagnostics GUI screenshot: $DiagnosticsScreenshotPath"
    Save-WindowScreenshotAtSize -Element $window -Process $process -Path $DiagnosticsNarrowScreenshotPath -Width 1440 -Height 1040 -TimeoutSeconds $TimeoutSeconds
    Write-Host "Diagnostics narrow GUI screenshot: $DiagnosticsNarrowScreenshotPath"

    $clearDiagnosticsTestsButton = Get-ByAutomationId -Root $window -AutomationId "ClearDiagnosticsTestsButton" -TimeoutSeconds $TimeoutSeconds
    Invoke-Element $clearDiagnosticsTestsButton
    Wait-Until -TimeoutSeconds $TimeoutSeconds -Message "Clear diagnostics tests did not reset the network result text." -Condition {
        $text = Get-ElementValue $resultBox
        if ($text -match "Not tested") {
            return $text
        }
        return $null
    } | Out-Null
    Wait-Until -TimeoutSeconds $TimeoutSeconds -Message "Clear diagnostics tests did not reset the direct route chip." -Condition {
        $text = Get-ElementValue $directRouteStatus
        if ($text -match "Direct: not tested") {
            return $text
        }
        return $null
    } | Out-Null
    Wait-Until -TimeoutSeconds $TimeoutSeconds -Message "Clear diagnostics tests did not reset the forward result text." -Condition {
        $text = Get-ElementValue $endpointResultBox
        if ($text -match "Not tested") {
            return $text
        }
        return $null
    } | Out-Null
    Wait-Until -TimeoutSeconds $TimeoutSeconds -Message "Clear diagnostics tests did not reset the forward route chip." -Condition {
        $text = Get-ElementValue $forwardRouteStatus
        if ($text -match "Forward TCP: not tested") {
            return $text
        }
        return $null
    } | Out-Null
    Wait-Until -TimeoutSeconds $TimeoutSeconds -Message "Clear diagnostics tests did not reset the forward last-run text." -Condition {
        $text = Get-ElementValue $forwardLastRunText
        if ($text -match "Forward test not run") {
            return $text
        }
        return $null
    } | Out-Null
    Write-Host "Diagnostics tests cleared"
    $runDiagnosticsButton = Get-ByAutomationId -Root $window -AutomationId "RunDiagnosticsButton" -TimeoutSeconds $TimeoutSeconds
    Invoke-Element $runDiagnosticsButton
    $diagnosticsRefreshedText = Wait-Until -TimeoutSeconds $TimeoutSeconds -Message "Run Checks did not report a diagnostics refresh." -Condition {
        $text = Get-ElementValue $appErrorText
        if ($text -match "Diagnostics refreshed") {
            return $text
        }
        return $null
    }
    $diagnosticsLastRunText = Get-ByAutomationId -Root $window -AutomationId "DiagnosticsLastRunText" -TimeoutSeconds $TimeoutSeconds
    $diagnosticsLastRun = Wait-Until -TimeoutSeconds $TimeoutSeconds -Message "Run Checks did not update the diagnostics last-run text." -Condition {
        $text = Get-ElementValue $diagnosticsLastRunText
        if ($text -match "Checks refreshed") {
            return $text
        }
        return $null
    }
    $diagnosticsSummaryBox = Get-ByAutomationId -Root $window -AutomationId "DiagnosticsSummaryTextBox" -TimeoutSeconds $TimeoutSeconds
    $diagnosticsReportBox = Get-ByAutomationId -Root $window -AutomationId "DiagnosticsReportTextBox" -TimeoutSeconds $TimeoutSeconds
    $diagnosticsSummaryAfterRunChecks = Wait-Until -TimeoutSeconds $TimeoutSeconds -Message "Run Checks did not populate the diagnostics summary." -Condition {
        $text = Get-ElementValue $diagnosticsSummaryBox
        if ($text -match "Profile: Local x-tunnel" -and $text -match "OS:") {
            return $text
        }
        return $null
    }
    Wait-Until -TimeoutSeconds $TimeoutSeconds -Message "Run Checks did not populate the diagnostics report JSON." -Condition {
        $text = Get-ElementValue $diagnosticsReportBox
        if ($text -match '"activeProfile"' -and $text -match '"createdAt"') {
            return $text
        }
        return $null
    } | Out-Null
    Write-Host "Diagnostics run checks: $diagnosticsRefreshedText / $diagnosticsLastRun / $($diagnosticsSummaryAfterRunChecks.Split([Environment]::NewLine)[0])"

    $connectButton = Get-ByAutomationId -Root $window -AutomationId "ConnectButton" -TimeoutSeconds $TimeoutSeconds
    Invoke-Element $connectButton

    $connectedText = Wait-Until -TimeoutSeconds $TimeoutSeconds -Message "GUI connect did not reach Connected or Degraded state." -Condition {
        $text = Get-ElementValue $statusState
        if ($text -match "Connected" -or $text -match "Degraded") {
            return $text
        }
        return $null
    }
    Write-Host "Connection state: $connectedText"
    $connectedCoreStatus = Wait-Until -TimeoutSeconds $TimeoutSeconds -Message "Status bar core field did not show a running core after connect." -Condition {
        $text = Get-ElementValue $statusCore
        if ($text -notmatch "Core not running" -and $text -match "/") {
            return $text
        }
        return $null
    }
    $connectedLocalProxy = Get-ElementValue $statusLocalProxy
    if ($connectedLocalProxy -notmatch "HTTP 127.0.0.1:" -or $connectedLocalProxy -notmatch "SOCKS 127.0.0.1:") {
        throw "Status bar local proxy changed unexpectedly after connect: '$connectedLocalProxy'"
    }
    $connectedTrafficStatus = Wait-Until -TimeoutSeconds $TimeoutSeconds -Message "Status bar traffic summary did not stay readable while connected." -Condition {
        $text = Get-ElementValue $statusTraffic
        if ($text -match "up / .* down") { return $text }
        return $null
    }
    $connectedChannelsStatus = Wait-Until -TimeoutSeconds $TimeoutSeconds -Message "Status bar channels summary did not show connected channels." -Condition {
        $text = Get-ElementValue $statusChannels
        if ($text -match "1/1 up") { return $text }
        return $null
    }
    Write-Host "Connected status bar: $connectedCoreStatus / $connectedLocalProxy / $connectedTrafficStatus / $connectedChannelsStatus"

    Select-Element $overviewTab
    $overviewRecentLogsBox = Get-ByAutomationId -Root $window -AutomationId "OverviewRecentLogsTextBox" -TimeoutSeconds $TimeoutSeconds
    $overviewRuntimeDetailsBox = Get-ByAutomationId -Root $window -AutomationId "OverviewRuntimeDetailsTextBox" -TimeoutSeconds $TimeoutSeconds
    $overviewRecentLogs = Wait-Until -TimeoutSeconds $TimeoutSeconds -Message "Overview recent logs did not show runtime log output after connect." -Condition {
        $text = Get-ElementValue $overviewRecentLogsBox
        if ($text -match "\[客户端\]" -or $text -match "metrics" -or $text -match "HTTP") {
            return $text
        }
        return $null
    }
    $overviewRuntimeDetails = Wait-Until -TimeoutSeconds $TimeoutSeconds -Message "Overview runtime details did not show status output after connect." -Condition {
        $text = Get-ElementValue $overviewRuntimeDetailsBox
        if ($text -match "version" -and $text -match "mode") {
            return $text
        }
        return $null
    }
    Write-Host "Overview connected details: $($overviewRecentLogs.Split([Environment]::NewLine)[0]) / $($overviewRuntimeDetails.Split([Environment]::NewLine)[0])"
    $copyOverviewRecentLogsButton = Get-ByAutomationId -Root $window -AutomationId "CopyOverviewRecentLogsButton" -TimeoutSeconds $TimeoutSeconds
    Clear-SmokeClipboard
    Invoke-Element $copyOverviewRecentLogsButton
    $clipboardOverviewLogs = Wait-Until -TimeoutSeconds $TimeoutSeconds -Message "Copy overview recent logs did not place logs on the clipboard." -Condition {
        $text = Get-Clipboard -Raw -ErrorAction SilentlyContinue
        if ($text -match "\[客户端\]" -or $text -match "metrics" -or $text -match "HTTP") {
            return $text
        }
        return $null
    }
    $copyOverviewRuntimeDetailsButton = Get-ByAutomationId -Root $window -AutomationId "CopyOverviewRuntimeDetailsButton" -TimeoutSeconds $TimeoutSeconds
    Clear-SmokeClipboard
    Invoke-Element $copyOverviewRuntimeDetailsButton
    $clipboardOverviewRuntime = Wait-Until -TimeoutSeconds $TimeoutSeconds -Message "Copy overview runtime details did not place status JSON on the clipboard." -Condition {
        $text = Get-Clipboard -Raw -ErrorAction SilentlyContinue
        if ($text -match "Status:" -and $text -match "Stats:" -and $text -match "version" -and $text -match "mode" -and $text -match "traffic") {
            return $text
        }
        return $null
    }
    $copyRuntimeMetricsButton = Get-ByAutomationId -Root $window -AutomationId "CopyRuntimeMetricsButton" -TimeoutSeconds $TimeoutSeconds
    Clear-SmokeClipboard
    Invoke-Element $copyRuntimeMetricsButton
    $clipboardRuntimeMetrics = Wait-Until -TimeoutSeconds $TimeoutSeconds -Message "Copy runtime metrics did not place Prometheus metrics on the clipboard." -Condition {
        $text = Get-Clipboard -Raw -ErrorAction SilentlyContinue
        if ($text -match "x_tunnel_") {
            return $text
        }
        return $null
    }
    $runtimeMetricsFirstLine = ($clipboardRuntimeMetrics -split "`r?`n")[0]
    Write-Host "Copied overview diagnostics: $($clipboardOverviewLogs.Split([Environment]::NewLine)[0]) / $($clipboardOverviewRuntime.Split([Environment]::NewLine)[0]) / $runtimeMetricsFirstLine"
    try {
        $scrollPattern = $copyOverviewRuntimeDetailsButton.GetCurrentPattern([System.Windows.Automation.ScrollItemPattern]::Pattern)
        $scrollPattern.ScrollIntoView()
    }
    catch {
    }
    Save-ElementScreenshot -Element $window -Path $OverviewRuntimeScreenshotPath
    Write-Host "Overview runtime GUI screenshot: $OverviewRuntimeScreenshotPath"

    $restartButton = Get-ByAutomationId -Root $window -AutomationId "RestartButton" -TimeoutSeconds $TimeoutSeconds
    Invoke-Element $restartButton
    Start-Sleep -Milliseconds 500
    $restartedText = Wait-Until -TimeoutSeconds $TimeoutSeconds -Message "GUI restart did not return to Connected or Degraded state." -Condition {
        $text = Get-ElementValue $statusState
        if ($text -match "Connected" -or $text -match "Degraded") {
            return $text
        }
        return $null
    }
    $restartedCoreStatus = Wait-Until -TimeoutSeconds $TimeoutSeconds -Message "Status bar core field did not show a running core after restart." -Condition {
        $text = Get-ElementValue $statusCore
        if ($text -notmatch "Core not running" -and $text -match "/") {
            return $text
        }
        return $null
    }
    Write-Host "Restarted state: $restartedText / $restartedCoreStatus"

    Invoke-Element $headerDiagnosticsButton
    $testButton = Get-ByAutomationId -Root $window -AutomationId "TestNetworkButton" -TimeoutSeconds $TimeoutSeconds
    Invoke-Element $testButton
    $connectedNetworkText = Wait-Until -TimeoutSeconds $TimeoutSeconds -Message "Connected network test did not report proxy success." -Condition {
        $text = Get-ElementValue $resultBox
        if ($text -match "Direct: ok status=204" -and $text -match "HTTP proxy: ok status=204") {
            return $text
        }
        return $null
    }
    $connectedProxyRouteText = Wait-Until -TimeoutSeconds $TimeoutSeconds -Message "Proxy route status chip did not show connected success." -Condition {
        $text = Get-ElementValue $proxyRouteStatus
        if ($text -match "HTTP proxy: ok") {
            return $text
        }
        return $null
    }
    Write-Host "Connected network test: $connectedNetworkText"
    Write-Host "Connected proxy route chip: $connectedProxyRouteText"
    $copyConnectedNetworkResultButton = Get-ByAutomationId -Root $window -AutomationId "CopyNetworkTestResultButton" -TimeoutSeconds $TimeoutSeconds
    Clear-SmokeClipboard
    Invoke-Element $copyConnectedNetworkResultButton
    $connectedNetworkClipboardText = Wait-Until -TimeoutSeconds $TimeoutSeconds -Message "Copy network result did not include the connected proxy success." -Condition {
        $text = Get-Clipboard -Raw -ErrorAction SilentlyContinue
        if ($text -match "Direct: ok status=204" -and $text -match "HTTP proxy: ok status=204") {
            return $text
        }
        return $null
    }
    Write-Host "Copied connected network result: $($connectedNetworkClipboardText.Split([Environment]::NewLine)[0])"

    $disconnectButton = Get-ByAutomationId -Root $window -AutomationId "DisconnectButton" -TimeoutSeconds $TimeoutSeconds
    Invoke-Element $disconnectButton

    $disconnectedText = Wait-Until -TimeoutSeconds $TimeoutSeconds -Message "GUI disconnect did not return to Disconnected state." -Condition {
        $text = Get-ElementValue $statusState
        if ($text -match "Disconnected") {
            return $text
        }
        return $null
    }
    Write-Host "Connection state: $disconnectedText"
    Wait-Until -TimeoutSeconds $TimeoutSeconds -Message "Status bar core field did not return to stopped after disconnect." -Condition {
        $text = Get-ElementValue $statusCore
        if ($text -match "Core not running") {
            return $text
        }
        return $null
    } | Out-Null

    $settingsTab = Get-ByAutomationId -Root $window -AutomationId "SettingsTab" -TimeoutSeconds $TimeoutSeconds
    Select-Element $settingsTab
    $settingsAutoConnectCheckBox = Get-ByAutomationId -Root $window -AutomationId "SettingsAutoConnectCheckBox" -TimeoutSeconds $TimeoutSeconds
    $settingsAutoConnectState = Get-ToggleState $settingsAutoConnectCheckBox
    if ($settingsAutoConnectState -ne "On") {
        throw "Settings auto-connect checkbox did not reflect the startup profile shortcut: '$settingsAutoConnectState'"
    }
    Write-Host "Settings auto-connect checkbox: $settingsAutoConnectState"
    $detectedCorePathText = Get-ByAutomationId -Root $window -AutomationId "DetectedCorePathTextBlock" -TimeoutSeconds $TimeoutSeconds
    $detectedCorePath = Get-ElementValue $detectedCorePathText
    if ($detectedCorePath -notmatch "x-tunnel.exe") {
        throw "Detected core path was not visible: '$detectedCorePath'"
    }
    $useDetectedCoreButton = Get-ByAutomationId -Root $window -AutomationId "UseDetectedCorePathButton" -TimeoutSeconds $TimeoutSeconds
    Invoke-Element $useDetectedCoreButton
    $settingsCorePathBox = Get-ByAutomationId -Root $window -AutomationId "SettingsCorePathTextBox" -TimeoutSeconds $TimeoutSeconds
    $settingsCorePath = Get-ElementValue $settingsCorePathBox
    if ($settingsCorePath -notmatch "x-tunnel.exe") {
        throw "Use Detected did not populate core path: '$settingsCorePath'"
    }
    $corePathStatusText = Get-ByAutomationId -Root $window -AutomationId "CorePathStatusText" -TimeoutSeconds $TimeoutSeconds
    $corePathStatus = Wait-Until -TimeoutSeconds $TimeoutSeconds -Message "Core path status did not report a valid configured path." -Condition {
        $text = Get-ElementValue $corePathStatusText
        if ($text -match "Configured") {
            return $text
        }
        return $null
    }
    $corePathStatusDetailText = Get-ByAutomationId -Root $window -AutomationId "CorePathStatusDetailText" -TimeoutSeconds $TimeoutSeconds
    Write-Host "Core path status: $corePathStatus / $(Get-ElementValue $corePathStatusDetailText)"
    $checkCoreVersionButton = Get-ByAutomationId -Root $window -AutomationId "CheckCoreVersionButton" -TimeoutSeconds $TimeoutSeconds
    Invoke-Element $checkCoreVersionButton
    $coreVersionDetail = Wait-Until -TimeoutSeconds $TimeoutSeconds -Message "Check core version did not report x-tunnel version output." -Condition {
        $status = Get-ElementValue $corePathStatusText
        $detail = Get-ElementValue $corePathStatusDetailText
        if ($status -match "Version OK" -and $detail -match "x-tunnel version=") {
            return $detail
        }
        return $null
    }
    Write-Host "Core version status: $coreVersionDetail"
    $copyCorePathButton = Get-ByAutomationId -Root $window -AutomationId "CopyCorePathButton" -TimeoutSeconds $TimeoutSeconds
    Clear-SmokeClipboard
    Invoke-Element $copyCorePathButton
    $clipboardCorePath = Wait-Until -TimeoutSeconds $TimeoutSeconds -Message "Copy core path did not place x-tunnel.exe on the clipboard." -Condition {
        $text = Get-Clipboard -Raw -ErrorAction SilentlyContinue
        if ($text -match [Regex]::Escape($settingsCorePath)) {
            return $text
        }
        return $null
    }
    Write-Host "Copied core path: $clipboardCorePath"

    $copySettingsFoldersButton = Get-ByAutomationId -Root $window -AutomationId "CopySettingsFoldersButton" -TimeoutSeconds $TimeoutSeconds
    Clear-SmokeClipboard
    Invoke-Element $copySettingsFoldersButton
    $clipboardSettingsFolders = Wait-Until -TimeoutSeconds $TimeoutSeconds -Message "Copy settings folders did not place local folder paths on the clipboard." -Condition {
        $text = Get-Clipboard -Raw -ErrorAction SilentlyContinue
        if ($text -match "App data:" -and
            $text -match "Profiles:" -and
            $text -match "Logs:" -and
            $text -match "Runtime:" -and
            $text -match "Core status: Version OK" -and
            $text -match "x-tunnel version=" -and
            $text -match [Regex]::Escape($AppHome) -and
            $text -match [Regex]::Escape($settingsCorePath)) {
            return $text
        }
        return $null
    }
    Write-Host "Copied settings folders: $($clipboardSettingsFolders.Split([Environment]::NewLine)[0])"

    $saveSettingsButton = Get-ByAutomationId -Root $window -AutomationId "SaveSettingsButton" -TimeoutSeconds $TimeoutSeconds
    Invoke-Element $saveSettingsButton
    $settingsSavedText = Wait-Until -TimeoutSeconds $TimeoutSeconds -Message "Save Settings did not report success." -Condition {
        $text = Get-ElementValue $appErrorText
        if ($text -match "Settings saved") {
            return $text
        }
        return $null
    }
    $savedCoreVersionStatus = Get-ElementValue $corePathStatusText
    $savedCoreVersionDetail = Get-ElementValue $corePathStatusDetailText
    if ($savedCoreVersionStatus -notmatch "Version OK" -or $savedCoreVersionDetail -notmatch "x-tunnel version=") {
        throw "Save Settings cleared the checked core version status: '$savedCoreVersionStatus' / '$savedCoreVersionDetail'"
    }
    Write-Host "Settings saved: $settingsSavedText"
    Start-Sleep -Milliseconds 250
    Save-ElementScreenshot -Element $window -Path $SettingsScreenshotPath
    Write-Host "Settings GUI screenshot: $SettingsScreenshotPath"

    $logsTab = Get-ByAutomationId -Root $window -AutomationId "LogsTab" -TimeoutSeconds $TimeoutSeconds
    Select-Element $logsTab
    $logLevelCombo = Get-ByAutomationId -Root $window -AutomationId "LogLevelFilterComboBox" -TimeoutSeconds $TimeoutSeconds
    if (!$logLevelCombo) {
        throw "Log level filter combo was not found."
    }
    $filteredLogBox = Get-ByAutomationId -Root $window -AutomationId "FilteredLogTextBox" -TimeoutSeconds $TimeoutSeconds
    $logText = Wait-Until -TimeoutSeconds $TimeoutSeconds -Message "Runtime logs did not populate after GUI connect." -Condition {
        $text = Get-ElementValue $filteredLogBox
        if (![string]::IsNullOrWhiteSpace($text)) {
            return $text
        }
        return $null
    }
    Write-Host "Logs populated: $($logText.Split([Environment]::NewLine)[0])"
    $logFilterBox = Get-ByAutomationId -Root $window -AutomationId "LogFilterTextBox" -TimeoutSeconds $TimeoutSeconds
    $logFilterSummaryText = Get-ByAutomationId -Root $window -AutomationId "LogFilterSummaryText" -TimeoutSeconds $TimeoutSeconds
    $logFilterBadgeText = Get-ByAutomationId -Root $window -AutomationId "LogFilterBadgeText" -TimeoutSeconds $TimeoutSeconds
    Set-ElementValue -Element $logFilterBox -Value "/protocol=v2-only/"
    $regexFilteredLogText = Wait-Until -TimeoutSeconds $TimeoutSeconds -Message "Regex log filter did not narrow the output." -Condition {
        $text = Get-ElementValue $filteredLogBox
        if ($text -match "protocol=v2-only" -and $text -notmatch "\[metrics\]") {
            return $text
        }
        return $null
    }
    $regexFilterSummary = Wait-Until -TimeoutSeconds $TimeoutSeconds -Message "Regex log filter summary did not show regex mode." -Condition {
        $text = Get-ElementValue $logFilterSummaryText
        if ($text -match "regex filter") {
            return $text
        }
        return $null
    }
    $regexFilterBadge = Wait-Until -TimeoutSeconds $TimeoutSeconds -Message "Regex log filter badge did not show regex mode." -Condition {
        $text = Get-ElementValue $logFilterBadgeText
        if ($text -match "Regex filter") {
            return $text
        }
        return $null
    }
    Write-Host "Regex log filter: $($regexFilteredLogText.Split([Environment]::NewLine)[0]) / $regexFilterSummary / $regexFilterBadge"
    Save-ElementScreenshot -Element $window -Path $LogsRegexScreenshotPath
    Write-Host "Regex logs GUI screenshot: $LogsRegexScreenshotPath"
    Set-ElementValue -Element $logFilterBox -Value "/[/"
    Wait-Until -TimeoutSeconds $TimeoutSeconds -Message "Invalid regex text did not appear in the log filter box before screenshot capture." -Condition {
        $text = Get-ElementValue $logFilterBox
        if ($text -eq "/[/") {
            return $text
        }
        return $null
    } | Out-Null
    $invalidRegexSummary = Wait-Until -TimeoutSeconds $TimeoutSeconds -Message "Invalid regex summary did not show the parse error." -Condition {
        $text = Get-ElementValue $logFilterSummaryText
        if ($text -match "Invalid regex") {
            return $text
        }
        return $null
    }
    $invalidRegexBadge = Wait-Until -TimeoutSeconds $TimeoutSeconds -Message "Invalid regex badge did not show an error state." -Condition {
        $text = Get-ElementValue $logFilterBadgeText
        if ($text -match "Invalid regex") {
            return $text
        }
        return $null
    }
    Write-Host "Invalid regex filter: $invalidRegexSummary / $invalidRegexBadge"
    Start-Sleep -Milliseconds 250
    Save-ElementScreenshot -Element $window -Path $LogsInvalidRegexScreenshotPath
    Write-Host "Invalid regex logs GUI screenshot: $LogsInvalidRegexScreenshotPath"
    Set-ElementValue -Element $logFilterBox -Value "unlikely-smoke-filter"
    Wait-Until -TimeoutSeconds $TimeoutSeconds -Message "Log text filter did not narrow the output." -Condition {
        $text = Get-ElementValue $filteredLogBox
        if ([string]::IsNullOrWhiteSpace($text)) {
            return "filtered"
        }
        return $null
    } | Out-Null
    $noMatchesBadge = Wait-Until -TimeoutSeconds $TimeoutSeconds -Message "No-match log filter badge did not show no matches." -Condition {
        $text = Get-ElementValue $logFilterBadgeText
        if ($text -match "No matches") {
            return $text
        }
        return $null
    }
    Write-Host "No-match log filter badge: $noMatchesBadge"
    $clearLogFiltersButton = Get-ByAutomationId -Root $window -AutomationId "ClearLogFiltersButton" -TimeoutSeconds $TimeoutSeconds
    Invoke-Element $clearLogFiltersButton
    $restoredLogText = Wait-Until -TimeoutSeconds $TimeoutSeconds -Message "Clear log filters did not restore log output." -Condition {
        $text = Get-ElementValue $filteredLogBox
        if (![string]::IsNullOrWhiteSpace($text)) {
            return $text
        }
        return $null
    }
    Wait-Until -TimeoutSeconds $TimeoutSeconds -Message "Clear log filters did not reset the filter badge." -Condition {
        $text = Get-ElementValue $logFilterBadgeText
        if ($text -match "All logs") {
            return $text
        }
        return $null
    } | Out-Null
    $copyFilteredLogsButton = Get-ByAutomationId -Root $window -AutomationId "CopyFilteredLogsButton" -TimeoutSeconds $TimeoutSeconds
    $restoredFirstLogLine = $restoredLogText.Split([Environment]::NewLine)[0]
    Clear-SmokeClipboard
    Invoke-Element $copyFilteredLogsButton
    $clipboardLogText = Wait-Until -TimeoutSeconds $TimeoutSeconds -Message "Copy filtered logs did not place the restored log output on the clipboard." -Condition {
        $text = Get-Clipboard -Raw -ErrorAction SilentlyContinue
        if ($text -match [Regex]::Escape($restoredFirstLogLine)) {
            return $text
        }
        return $null
    }
    Write-Host "Copied filtered logs: $($clipboardLogText.Split([Environment]::NewLine)[0])"
    Save-ElementScreenshot -Element $window -Path $LogsScreenshotPath
    Write-Host "Logs GUI screenshot: $LogsScreenshotPath"
    $logsRefreshDiagnosticsButton = Get-ByAutomationId -Root $window -AutomationId "LogsRefreshDiagnosticsButton" -TimeoutSeconds $TimeoutSeconds
    Invoke-Element $logsRefreshDiagnosticsButton
    $logsDiagnosticsRefreshText = Wait-Until -TimeoutSeconds $TimeoutSeconds -Message "Logs Refresh Diagnostics did not report a diagnostics refresh." -Condition {
        $text = Get-ElementValue $appErrorText
        if ($text -match "Diagnostics refreshed") {
            return $text
        }
        return $null
    }
    Write-Host "Logs diagnostics refresh: $logsDiagnosticsRefreshText"
    $exportDiagnosticsButton = Get-ByAutomationId -Root $window -AutomationId "ExportDiagnosticsButton" -TimeoutSeconds $TimeoutSeconds
    Invoke-Element $exportDiagnosticsButton
    $logsExportedDiagnosticsText = Wait-Until -TimeoutSeconds $TimeoutSeconds -Message "Logs Export Diagnostics did not report an exported file path." -Condition {
        $text = Get-ElementValue $appErrorText
        if ($text -match "Diagnostics exported: .*\.zip") {
            return $text
        }
        return $null
    }
    $logsExportedDiagnosticsPath = $logsExportedDiagnosticsText -replace '^Diagnostics exported:\s*', ''
    if (!(Test-Path $logsExportedDiagnosticsPath)) {
        throw "Logs exported diagnostics zip was not created: '$logsExportedDiagnosticsPath'"
    }
    $logsZip = [System.IO.Compression.ZipFile]::OpenRead($logsExportedDiagnosticsPath)
    try {
        if (!($logsZip.Entries | Where-Object { $_.FullName -eq "report.json" })) {
            throw "Logs exported diagnostics zip did not contain report.json: '$logsExportedDiagnosticsPath'"
        }
    }
    finally {
        $logsZip.Dispose()
    }
    Write-Host "Logs exported diagnostics zip: $logsExportedDiagnosticsPath"

    $subscriptionsTab = Get-ByAutomationId -Root $window -AutomationId "SubscriptionsTab" -TimeoutSeconds $TimeoutSeconds
    Select-Element $subscriptionsTab

    $newSubscriptionButton = Get-ByAutomationId -Root $window -AutomationId "NewSubscriptionButton" -TimeoutSeconds $TimeoutSeconds
    Invoke-Element $newSubscriptionButton

    $subscriptionNameBox = Get-ByAutomationId -Root $window -AutomationId "SubscriptionNameTextBox" -TimeoutSeconds $TimeoutSeconds
    Set-ElementValue -Element $subscriptionNameBox -Value "Smoke subscription"

    $subscriptionUrlBox = Get-ByAutomationId -Root $window -AutomationId "SubscriptionUrlTextBox" -TimeoutSeconds $TimeoutSeconds
    Set-ElementValue -Element $subscriptionUrlBox -Value $subscriptionUrl

    $subscriptionStatusBox = Get-ByAutomationId -Root $window -AutomationId "SubscriptionStatusTextBox" -TimeoutSeconds $TimeoutSeconds
    $saveSubscriptionDetailsButton = Get-ByAutomationId -Root $window -AutomationId "SaveSubscriptionDetailsButton" -TimeoutSeconds $TimeoutSeconds
    Invoke-Element $saveSubscriptionDetailsButton
    $subscriptionSavedText = Wait-Until -TimeoutSeconds $TimeoutSeconds -Message "Save Subscription did not report success." -Condition {
        $text = Get-ElementValue $subscriptionStatusBox
        if ($text -match "Subscription saved") {
            return $text
        }
        return $null
    }
    Write-Host "Subscription saved: $subscriptionSavedText"

    $updateSubscriptionButton = Get-ByAutomationId -Root $window -AutomationId "UpdateSubscriptionNowButton" -TimeoutSeconds $TimeoutSeconds
    Invoke-Element $updateSubscriptionButton

    $subscriptionText = Wait-Until -TimeoutSeconds $TimeoutSeconds -Message "Subscription update did not add a profile." -Condition {
        $text = Get-ElementValue $subscriptionStatusBox
        if ($text -match "added 1" -and $text -match "updated 0") {
            return $text
        }
        return $null
    }
    Write-Host $subscriptionText
    $updateAllSubscriptionsButton = Get-ByAutomationId -Root $window -AutomationId "UpdateAllSubscriptionsNowButton" -TimeoutSeconds $TimeoutSeconds
    Invoke-Element $updateAllSubscriptionsButton
    $updateAllText = Wait-Until -TimeoutSeconds $TimeoutSeconds -Message "Update All subscriptions did not report the aggregate result." -Condition {
        $text = Get-ElementValue $subscriptionStatusBox
        if ($text -match "Updated all 1 subscription" -and $text -match "Succeeded: 1, failed: 0" -and $text -match "unchanged: 1") {
            return $text
        }
        return $null
    }
    Write-Host $updateAllText
    $copySubscriptionSourceButton = Get-ByAutomationId -Root $window -AutomationId "CopySubscriptionSourceButton" -TimeoutSeconds $TimeoutSeconds
    Clear-SmokeClipboard
    Invoke-Element $copySubscriptionSourceButton
    $clipboardSubscriptionSource = Wait-Until -TimeoutSeconds $TimeoutSeconds -Message "Copy subscription source did not place the source summary on the clipboard." -Condition {
        $text = Get-Clipboard -Raw -ErrorAction SilentlyContinue
        if ($text -match "Subscription: Smoke subscription" -and $text -match [Regex]::Escape($subscriptionUrl) -and $text -match "Interval:") {
            return $text
        }
        return $null
    }
    Write-Host "Copied subscription source: $($clipboardSubscriptionSource.Split([Environment]::NewLine)[0])"
    $copySubscriptionStatusButton = Get-ByAutomationId -Root $window -AutomationId "CopySubscriptionStatusButton" -TimeoutSeconds $TimeoutSeconds
    Clear-SmokeClipboard
    Invoke-Element $copySubscriptionStatusButton
    $clipboardSubscriptionStatus = Wait-Until -TimeoutSeconds $TimeoutSeconds -Message "Copy subscription result did not place the aggregate result on the clipboard." -Condition {
        $text = Get-Clipboard -Raw -ErrorAction SilentlyContinue
        if ($text -match "Updated all 1 subscription" -and $text -match "Succeeded: 1, failed: 0" -and $text -match "unchanged: 1") {
            return $text
        }
        return $null
    }
    Write-Host "Copied subscription result: $($clipboardSubscriptionStatus.Split([Environment]::NewLine)[0])"
    $subscriptionSummaryBlock = Get-ByAutomationId -Root $window -AutomationId "SubscriptionSummaryTextBlock" -TimeoutSeconds $TimeoutSeconds
    $subscriptionSummaryText = Wait-Until -TimeoutSeconds $TimeoutSeconds -Message "Subscription summary did not reflect the updated subscription." -Condition {
        $text = Get-ElementValue $subscriptionSummaryBlock
        if ($text -match "Subscriptions 1/1 visible" -and $text -match "1 updated" -and $text -match "0 failed") {
            return $text
        }
        return $null
    }
    Write-Host "Subscription summary: $subscriptionSummaryText"
    $subscriptionListState = Wait-Until -TimeoutSeconds $TimeoutSeconds -Message "Subscription list item did not show Updated state." -Condition {
        $items = Find-AllByAutomationId -Root $window -AutomationId "SubscriptionListItemState"
        foreach ($item in $items) {
            $text = Get-ElementValue $item
            if ($text -match "Updated") {
                return $text
            }
        }
        return $null
    }
    Write-Host "Subscription list state: $subscriptionListState"
    $subscriptionSearchBox = Get-ByAutomationId -Root $window -AutomationId "SubscriptionSearchTextBox" -TimeoutSeconds $TimeoutSeconds
    Set-ElementValue -Element $subscriptionSearchBox -Value "Smoke"
    $searchedSubscriptionText = Wait-Until -TimeoutSeconds $TimeoutSeconds -Message "Subscription search did not keep the smoke subscription visible." -Condition {
        $item = Find-ByAutomationId -Root $window -AutomationId "SubscriptionListItemName"
        if (!$item) {
            return $null
        }
        $text = Get-ElementValue $item
        if ($text -match "Smoke subscription") {
            return $text
        }
        return $null
    }
    Write-Host "Subscription search result: $searchedSubscriptionText"
    Set-ElementValue -Element $subscriptionSearchBox -Value "missing-subscription-filter"
    Wait-Until -TimeoutSeconds $TimeoutSeconds -Message "Subscription search did not reduce visible subscriptions to zero." -Condition {
        $text = Get-ElementValue $subscriptionSummaryBlock
        if ($text -match "Subscriptions 0/1 visible") {
            return $text
        }
        return $null
    } | Out-Null
    $clearSubscriptionSearchButton = Get-ByAutomationId -Root $window -AutomationId "ClearSubscriptionSearchButton" -TimeoutSeconds $TimeoutSeconds
    Invoke-Element $clearSubscriptionSearchButton
    $clearedSubscriptionText = Wait-Until -TimeoutSeconds $TimeoutSeconds -Message "Clear subscription search did not restore the smoke subscription." -Condition {
        $item = Find-ByAutomationId -Root $window -AutomationId "SubscriptionListItemName"
        if (!$item) {
            return $null
        }
        $text = Get-ElementValue $item
        if ($text -match "Smoke subscription") {
            return $text
        }
        return $null
    }
    Write-Host "Subscription search cleared: $clearedSubscriptionText"
    $subscriptionSortCombo = Get-ByAutomationId -Root $window -AutomationId "SubscriptionSortComboBox" -TimeoutSeconds $TimeoutSeconds
    Select-ComboBoxItem -Element $subscriptionSortCombo -Name "Updated" -TimeoutSeconds $TimeoutSeconds
    $updatedSortedSubscription = Wait-Until -TimeoutSeconds $TimeoutSeconds -Message "Updated subscription sort did not keep the updated subscription visible." -Condition {
        $item = Find-ByAutomationId -Root $window -AutomationId "SubscriptionListItemName"
        if (!$item) {
            return $null
        }
        $text = Get-ElementValue $item
        if ($text -match "Smoke subscription") {
            return $text
        }
        return $null
    }
    Write-Host "Subscription updated sort first item: $updatedSortedSubscription"
    Save-ElementScreenshot -Element $window -Path $SubscriptionScreenshotPath
    Write-Host "Subscription GUI screenshot: $SubscriptionScreenshotPath"
    $deleteSubscriptionButton = Get-ByAutomationId -Root $window -AutomationId "DeleteSubscriptionButton" -TimeoutSeconds $TimeoutSeconds
    Invoke-Element $deleteSubscriptionButton
    $deletedSubscriptionStatus = Wait-Until -TimeoutSeconds $TimeoutSeconds -Message "Delete subscription did not update the subscription status text." -Condition {
        $text = Get-ElementValue $subscriptionStatusBox
        if ($text -match "Subscription deleted") {
            return $text
        }
        return $null
    }
    $deletedSubscriptionSummary = Wait-Until -TimeoutSeconds $TimeoutSeconds -Message "Delete subscription did not reset the subscription summary." -Condition {
        $text = Get-ElementValue $subscriptionSummaryBlock
        if ($text -match "Subscriptions 0/0 visible") {
            return $text
        }
        return $null
    }
    Write-Host "Subscription deleted: $deletedSubscriptionStatus / $deletedSubscriptionSummary"

    $profilesTab = Get-ByAutomationId -Root $window -AutomationId "ProfilesTab" -TimeoutSeconds $TimeoutSeconds
    Select-Element $profilesTab
    $profileSearchBox = Get-ByAutomationId -Root $window -AutomationId "ProfileSearchTextBox" -TimeoutSeconds $TimeoutSeconds
    Set-ElementValue -Element $profileSearchBox -Value "Smoke"
    $searchedProfileText = Wait-Until -TimeoutSeconds $TimeoutSeconds -Message "Profile search did not show the subscription profile." -Condition {
        $item = Find-ByAutomationId -Root $window -AutomationId "ProfileListItemName"
        if (!$item) {
            return $null
        }
        $text = Get-ElementValue $item
        if ($text -match "Smoke subscription profile") {
            return $text
        }
        return $null
    }
    Write-Host "Profile search result: $searchedProfileText"
    $clearProfileSearchButton = Get-ByAutomationId -Root $window -AutomationId "ClearProfileSearchButton" -TimeoutSeconds $TimeoutSeconds
    Invoke-Element $clearProfileSearchButton
    $clearedProfileText = Wait-Until -TimeoutSeconds $TimeoutSeconds -Message "Clear profile search did not restore the local profile." -Condition {
        $item = Find-ByAutomationId -Root $window -AutomationId "ProfileListItemName"
        if (!$item) {
            return $null
        }
        $text = Get-ElementValue $item
        if ($text -match "Local x-tunnel") {
            return $text
        }
        return $null
    }
    Write-Host "Profile search cleared: $clearedProfileText"

    $profileSortCombo = Get-ByAutomationId -Root $window -AutomationId "ProfileSortComboBox" -TimeoutSeconds $TimeoutSeconds
    Select-ComboBoxItem -Element $profileSortCombo -Name "Endpoint" -TimeoutSeconds $TimeoutSeconds
    $endpointSortedProfile = Wait-Until -TimeoutSeconds $TimeoutSeconds -Message "Endpoint sort did not put the tested local profile first." -Condition {
        $item = Find-ByAutomationId -Root $window -AutomationId "ProfileListItemName"
        if (!$item) {
            return $null
        }
        $text = Get-ElementValue $item
        if ($text -match "Local x-tunnel") {
            return $text
        }
        return $null
    }
    Write-Host "Endpoint sort first profile: $endpointSortedProfile"

    $selectFastestButton = Get-ByAutomationId -Root $window -AutomationId "SelectFastestProfileButton" -TimeoutSeconds $TimeoutSeconds
    Invoke-Element $selectFastestButton
    $profileNameBox = Get-ByAutomationId -Root $window -AutomationId "ProfileNameTextBox" -TimeoutSeconds $TimeoutSeconds
    $fastestProfileName = Wait-Until -TimeoutSeconds $TimeoutSeconds -Message "Select fastest did not switch to the tested local profile." -Condition {
        $text = Get-ElementValue $profileNameBox
        if ($text -match "Local x-tunnel") {
            return $text
        }
        return $null
    }
    $fastestText = Wait-Until -TimeoutSeconds $TimeoutSeconds -Message "Select fastest did not report the selected profile." -Condition {
        $text = Get-ElementValue $profileBatchTestTextBlock
        if ($text -match "Selected fastest: Local x-tunnel") {
            return $text
        }
        return $null
    }
    Write-Host "Fastest profile selected: $fastestProfileName / $fastestText"

    $profileJsonBox = Get-ByAutomationId -Root $window -AutomationId "ProfileJsonTextBox" -TimeoutSeconds $TimeoutSeconds
    Set-ElementValue -Element $profileJsonBox -Value "{"

    $saveProfileButton = Get-ByAutomationId -Root $window -AutomationId "SaveProfileButton" -TimeoutSeconds $TimeoutSeconds
    Invoke-Element $saveProfileButton

    $profileError = Wait-Until -TimeoutSeconds $TimeoutSeconds -Message "Invalid profile save did not surface an error in the GUI." -Condition {
        $text = Get-ElementValue $appErrorText
        if ($text -match "JSON" -or $text -match "配置") {
            return $text
        }
        return $null
    }
    Write-Host $profileError
    $profileValidationText = Wait-Until -TimeoutSeconds $TimeoutSeconds -Message "Profile list item did not show Issue after invalid save." -Condition {
        $items = Find-AllByAutomationId -Root $window -AutomationId "ProfileListItemValidationState"
        foreach ($item in $items) {
            $text = Get-ElementValue $item
            if ($text -match "Issue") {
                return $text
            }
        }
        return $null
    }
    Write-Host "Profile list state: $profileValidationText"
    $profileSummaryBlock = Get-ByAutomationId -Root $window -AutomationId "ProfileSummaryTextBlock" -TimeoutSeconds $TimeoutSeconds
    $profileSummaryText = Wait-Until -TimeoutSeconds $TimeoutSeconds -Message "Profile summary did not reflect issue and endpoint counts." -Condition {
        $text = Get-ElementValue $profileSummaryBlock
        if ($text -match "Profiles 2/2 visible" -and $text -match "1 issues" -and $text -match "1 endpoint ok") {
            return $text
        }
        return $null
    }
    Write-Host "Profile summary: $profileSummaryText"

    $copyProfileIssuesButton = Get-ByAutomationId -Root $window -AutomationId "CopyProfileIssuesButton" -TimeoutSeconds $TimeoutSeconds
    Clear-SmokeClipboard
    Invoke-Element $copyProfileIssuesButton
    $profileIssuesClipboardText = Wait-Until -TimeoutSeconds $TimeoutSeconds -Message "Copy profile issues did not place the validation issue report on the clipboard." -Condition {
        $text = Get-Clipboard -Raw -ErrorAction SilentlyContinue
        if ($text -match "Profile: Local x-tunnel" -and
            $text -match "Validation: Issue" -and
            ($text -match "配置 JSON 无效" -or $text -match "Expected depth" -or $text -match "profile \[error\]")) {
            return $text
        }
        return $null
    }
    Write-Host "Copied profile issues: $($profileIssuesClipboardText.Split([Environment]::NewLine)[0])"

    Save-ElementScreenshot -Element $window -Path $ScreenshotPath
    Write-Host "GUI screenshot: $ScreenshotPath"
    Save-WindowScreenshotAtSize -Element $window -Process $process -Path $ProfileNarrowScreenshotPath -Width 1440 -Height 1040 -TimeoutSeconds $TimeoutSeconds
    Write-Host "Profiles narrow GUI screenshot: $ProfileNarrowScreenshotPath"

    $clearEndpointTestsButton = Get-ByAutomationId -Root $window -AutomationId "ClearEndpointTestsButton" -TimeoutSeconds $TimeoutSeconds
    Invoke-Element $clearEndpointTestsButton
    $clearedEndpointSummary = Wait-Until -TimeoutSeconds $TimeoutSeconds -Message "Clear endpoint tests did not reset summary counts." -Condition {
        $text = Get-ElementValue $profileSummaryBlock
        if ($text -match "Profiles 2/2 visible" -and $text -match "0 endpoint ok") {
            return $text
        }
        return $null
    }
    Write-Host "Endpoint tests cleared: $clearedEndpointSummary"

    $importProfileClipboardButton = Get-ByAutomationId -Root $window -AutomationId "ImportProfileClipboardButton" -TimeoutSeconds $TimeoutSeconds
    $importProfileJson = $profileConfigClipboardText -replace '"token_ref"\s*:\s*"secret:profile-token"', '"token": "clipboard-token"'
    Set-SmokeClipboardText -Value $importProfileJson
    Invoke-Element $importProfileClipboardButton
    $importedProfileName = Wait-Until -TimeoutSeconds $TimeoutSeconds -Message "Import profile from clipboard did not select the imported profile." -Condition {
        $nameBox = Find-ByAutomationId -Root $window -AutomationId "ProfileNameTextBox"
        if (!$nameBox) {
            return $null
        }
        $text = Get-ElementValue $nameBox
        if ($text -match "Clipboard profile") {
            return $text
        }
        return $null
    }
    $importedProfileValidation = Wait-Until -TimeoutSeconds $TimeoutSeconds -Message "Imported clipboard profile did not finish validation." -Condition {
        $text = Get-ElementValue $appErrorText
        if ($text -match "Profile ready") {
            return $text
        }
        return $null
    }
    $importedProfileSummary = Wait-Until -TimeoutSeconds $TimeoutSeconds -Message "Imported clipboard profile did not appear in the profile summary." -Condition {
        $text = Get-ElementValue $profileSummaryBlock
        if ($text -match "Profiles 3/3 visible" -and $text -match "1 ready" -and $text -match "1 issues") {
            return $text
        }
        return $null
    }
    Write-Host "Clipboard profile imported: $importedProfileName / $importedProfileValidation / $importedProfileSummary"

    $duplicateProfileButton = Get-ByAutomationId -Root $window -AutomationId "DuplicateProfileButton" -TimeoutSeconds $TimeoutSeconds
    Invoke-Element $duplicateProfileButton
    $duplicatedProfileName = Wait-Until -TimeoutSeconds $TimeoutSeconds -Message "Duplicate profile did not select the copied profile." -Condition {
        $nameBox = Find-ByAutomationId -Root $window -AutomationId "ProfileNameTextBox"
        if (!$nameBox) {
            return $null
        }
        $text = Get-ElementValue $nameBox
        if ($text -match "Clipboard profile Copy") {
            return $text
        }
        return $null
    }
    $duplicatedProfileSummary = Wait-Until -TimeoutSeconds $TimeoutSeconds -Message "Duplicate profile did not update the profile count." -Condition {
        $text = Get-ElementValue $profileSummaryBlock
        if ($text -match "Profiles 4/4 visible") {
            return $text
        }
        return $null
    }
    Write-Host "Profile duplicated: $duplicatedProfileName / $duplicatedProfileSummary"

    $deleteProfileButton = Get-ByAutomationId -Root $window -AutomationId "DeleteProfileButton" -TimeoutSeconds $TimeoutSeconds
    Invoke-Element $deleteProfileButton
    $deletedProfileSummary = Wait-Until -TimeoutSeconds $TimeoutSeconds -Message "Delete profile did not remove the copied profile." -Condition {
        $text = Get-ElementValue $profileSummaryBlock
        if ($text -match "Profiles 3/3 visible" -and $text -notmatch "Profiles 4/4 visible") {
            return $text
        }
        return $null
    }
    Write-Host "Profile copy deleted: $deletedProfileSummary"

    $profilesTab = Get-ByAutomationId -Root $window -AutomationId "ProfilesTab" -TimeoutSeconds $TimeoutSeconds
    Select-Element $profilesTab
    $clearStartupBeforeRestartButton = Get-ByAutomationId -Root $window -AutomationId "ClearStartupProfileButton" -TimeoutSeconds $TimeoutSeconds
    if ($clearStartupBeforeRestartButton.Current.IsEnabled) {
        Invoke-Element $clearStartupBeforeRestartButton
        $startupClearedBeforeRestart = Wait-Until -TimeoutSeconds $TimeoutSeconds -Message "Startup profile did not clear before restart." -Condition {
            $text = Get-ElementValue $startupProfileSummaryBlock
            if ($text -match "Startup: not set") {
                return $text
            }
            return $null
        }
        $settingsTab = Get-ByAutomationId -Root $window -AutomationId "SettingsTab" -TimeoutSeconds $TimeoutSeconds
        Select-Element $settingsTab
        $saveSettingsButton = Get-ByAutomationId -Root $window -AutomationId "SaveSettingsButton" -TimeoutSeconds $TimeoutSeconds
        Invoke-Element $saveSettingsButton
        Wait-Until -TimeoutSeconds $TimeoutSeconds -Message "Clearing startup profile was not saved before restart." -Condition {
            $text = Get-ElementValue $appErrorText
            if ($text -match "Settings saved") {
                return $text
            }
            return $null
        } | Out-Null
        Write-Host "Startup profile cleared before restart: $startupClearedBeforeRestart"
    }

    if ($process -and !$process.HasExited) {
        Stop-Process -Id $process.Id -Force
        Wait-Process -Id $process.Id -Timeout 5 -ErrorAction SilentlyContinue | Out-Null
    }
    $env:XTUNNEL_CLIENT_HOME = $AppHome
    $env:XTUNNEL_CLIENT_INSTANCE = "$InstanceName-restart"
    $process = Start-Process -FilePath (Resolve-Path $AppExe).Path -PassThru
    $env:XTUNNEL_CLIENT_HOME = $oldHome
    $env:XTUNNEL_CLIENT_INSTANCE = $oldInstance

    $window = Wait-Until -TimeoutSeconds $TimeoutSeconds -Message "Timed out waiting for restarted x-tunnel Client window." -Condition {
        $condition = [System.Windows.Automation.PropertyCondition]::new(
            [System.Windows.Automation.AutomationElement]::ProcessIdProperty,
            $process.Id)
        $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $condition)
    }
    $diagnosticsTab = Get-ByAutomationId -Root $window -AutomationId "DiagnosticsTab" -TimeoutSeconds $TimeoutSeconds
    Select-Element $diagnosticsTab
    $persistedTargetCombo = Get-ByAutomationId -Root $window -AutomationId "NetworkTestTargetComboBox" -TimeoutSeconds $TimeoutSeconds
    $persistedTarget = Wait-Until -TimeoutSeconds $TimeoutSeconds -Message "Network test target did not persist after restart." -Condition {
        $text = Get-ElementValue $persistedTargetCombo
        if ($text -match "Custom") {
            return $text
        }
        return $null
    }
    $persistedUrlBox = Get-ByAutomationId -Root $window -AutomationId "NetworkTestUrlTextBox" -TimeoutSeconds $TimeoutSeconds
    $persistedUrl = Wait-Until -TimeoutSeconds $TimeoutSeconds -Message "Network test URL did not persist after restart." -Condition {
        $text = Get-ElementValue $persistedUrlBox
        if ($text -eq $testUrl) {
            return $text
        }
        return $null
    }
    Write-Host "Network target persisted after restart: $persistedTarget / $persistedUrl"

    $settingsTab = Get-ByAutomationId -Root $window -AutomationId "SettingsTab" -TimeoutSeconds $TimeoutSeconds
    Select-Element $settingsTab
    $languageCombo = Get-ByAutomationId -Root $window -AutomationId "LanguageComboBox" -TimeoutSeconds $TimeoutSeconds
    $initialLanguage = Wait-Until -TimeoutSeconds $TimeoutSeconds -Message "Language selector did not default to English." -Condition {
        $text = Get-ElementValue $languageCombo
        if ($text -match "English") {
            return $text
        }
        return $null
    }
    Write-Host "Initial language: $initialLanguage"
    Select-ComboBoxItem -Element $languageCombo -Name "中文 (简体)" -TimeoutSeconds $TimeoutSeconds
    $languageLabel = Get-ByAutomationId -Root $window -AutomationId "SettingsLanguageLabel" -TimeoutSeconds $TimeoutSeconds
    $languageLabelText = Wait-Until -TimeoutSeconds $TimeoutSeconds -Message "Language switch did not update Settings label to Chinese." -Condition {
        $text = Get-ElementValue $languageLabel
        if ($text -eq "语言") {
            return $text
        }
        return $null
    }
    $themeCombo = Get-ByAutomationId -Root $window -AutomationId "ThemeComboBox" -TimeoutSeconds $TimeoutSeconds
    $localizedThemeText = Wait-Until -TimeoutSeconds $TimeoutSeconds -Message "Theme option did not localize after switching to Chinese." -Condition {
        $text = Get-ElementValue $themeCombo
        if ($text -eq "跟随系统") {
            return $text
        }
        return $null
    }
    $updateChannelCombo = Get-ByAutomationId -Root $window -AutomationId "UpdateChannelComboBox" -TimeoutSeconds $TimeoutSeconds
    $localizedUpdateChannelText = Wait-Until -TimeoutSeconds $TimeoutSeconds -Message "Update channel option did not localize after switching to Chinese." -Condition {
        $text = Get-ElementValue $updateChannelCombo
        if ($text -eq "稳定版") {
            return $text
        }
        return $null
    }
    $profilesTab = Get-ByAutomationId -Root $window -AutomationId "ProfilesTab" -TimeoutSeconds $TimeoutSeconds
    Select-Element $profilesTab
    $profileSummary = Get-ByAutomationId -Root $window -AutomationId "ProfileSummaryTextBlock" -TimeoutSeconds $TimeoutSeconds
    $localizedProfileSummary = Wait-Until -TimeoutSeconds $TimeoutSeconds -Message "Profile summary did not localize after switching to Chinese." -Condition {
        $text = Get-ElementValue $profileSummary
        if ($text -match "^配置 ") {
            return $text
        }
        return $null
    }
    $profileState = Get-ByAutomationId -Root $window -AutomationId "ProfileListItemValidationState" -TimeoutSeconds $TimeoutSeconds
    $localizedProfileState = Wait-Until -TimeoutSeconds $TimeoutSeconds -Message "Profile list state did not localize after switching to Chinese." -Condition {
        $text = Get-ElementValue $profileState
        if ($text -match "^(就绪|问题|未检查)$") {
            return $text
        }
        return $null
    }
    $copyProfileSummaryButton = Get-ByAutomationId -Root $window -AutomationId "CopyProfileSummaryButton" -TimeoutSeconds $TimeoutSeconds
    Invoke-Element $copyProfileSummaryButton
    $appErrorText = Get-ByAutomationId -Root $window -AutomationId "AppErrorTextBlock" -TimeoutSeconds $TimeoutSeconds
    $localizedProfileCopyText = Wait-Until -TimeoutSeconds $TimeoutSeconds -Message "Localized profile copy feedback did not appear after switching to Chinese." -Condition {
        $text = Get-ElementValue $appErrorText
        if ($text -match "配置摘要已复制") {
            return $text
        }
        return $null
    }
    $subscriptionsTab = Get-ByAutomationId -Root $window -AutomationId "SubscriptionsTab" -TimeoutSeconds $TimeoutSeconds
    Select-Element $subscriptionsTab
    $subscriptionSummary = Get-ByAutomationId -Root $window -AutomationId "SubscriptionSummaryTextBlock" -TimeoutSeconds $TimeoutSeconds
    $localizedSubscriptionSummary = Wait-Until -TimeoutSeconds $TimeoutSeconds -Message "Subscription summary did not localize after switching to Chinese." -Condition {
        $text = Get-ElementValue $subscriptionSummary
        if ($text -match "^订阅 ") {
            return $text
        }
        return $null
    }
    $newLocalizedSubscriptionButton = Get-ByAutomationId -Root $window -AutomationId "NewSubscriptionButton" -TimeoutSeconds $TimeoutSeconds
    Invoke-Element $newLocalizedSubscriptionButton
    $localizedSubscriptionStatusBox = Get-ByAutomationId -Root $window -AutomationId "SubscriptionStatusTextBox" -TimeoutSeconds $TimeoutSeconds
    $localizedNewSubscriptionStatus = Wait-Until -TimeoutSeconds $TimeoutSeconds -Message "New subscription status did not localize after switching to Chinese." -Condition {
        $text = Get-ElementValue $localizedSubscriptionStatusBox
        if ($text -match "上次结果: 未保存") {
            return $text
        }
        return $null
    }
    $copyLocalizedSubscriptionSourceButton = Get-ByAutomationId -Root $window -AutomationId "CopySubscriptionSourceButton" -TimeoutSeconds $TimeoutSeconds
    Wait-Until -TimeoutSeconds $TimeoutSeconds -Message "Localized subscription source copy button did not become enabled." -Condition {
        if ($copyLocalizedSubscriptionSourceButton.Current.IsEnabled) {
            return $true
        }
        return $null
    } | Out-Null
    Clear-SmokeClipboard
    Invoke-Element $copyLocalizedSubscriptionSourceButton
    $localizedSubscriptionSourceClipboard = Wait-Until -TimeoutSeconds $TimeoutSeconds -Message "Localized subscription source was not copied after switching to Chinese." -Condition {
        $text = Get-Clipboard -Raw -ErrorAction SilentlyContinue
        if ($text -match "^订阅:" -and
            $text -match "间隔:" -and
            $text -match "上次结果: 未保存") {
            return $text
        }
        return $null
    }
    $localizedSubscriptionSourceCopyText = Wait-AutomationTextMatch -Root $window -AutomationId "AppErrorTextBlock" -Pattern "订阅来源已复制" -TimeoutSeconds $TimeoutSeconds -Message "Localized subscription source copy feedback did not appear after switching to Chinese."
    $copyLocalizedSubscriptionStatusButton = Get-ByAutomationId -Root $window -AutomationId "CopySubscriptionStatusButton" -TimeoutSeconds $TimeoutSeconds
    Wait-Until -TimeoutSeconds $TimeoutSeconds -Message "Localized subscription result copy button did not become enabled." -Condition {
        if ($copyLocalizedSubscriptionStatusButton.Current.IsEnabled) {
            return $true
        }
        return $null
    } | Out-Null
    Clear-SmokeClipboard
    Invoke-Element $copyLocalizedSubscriptionStatusButton
    $localizedSubscriptionStatusClipboard = Wait-Until -TimeoutSeconds $TimeoutSeconds -Message "Localized subscription result was not copied after switching to Chinese." -Condition {
        $text = Get-Clipboard -Raw -ErrorAction SilentlyContinue
        if ($text -match "上次结果: 未保存" -and
            $text -match "更新时间:") {
            return $text
        }
        return $null
    }
    $localizedSubscriptionStatusCopyText = Wait-AutomationTextMatch -Root $window -AutomationId "AppErrorTextBlock" -Pattern "订阅结果已复制" -TimeoutSeconds $TimeoutSeconds -Message "Localized subscription result copy feedback did not appear after switching to Chinese."
    Select-Element $settingsTab
    $localizedCorePathStatusText = Get-ByAutomationId -Root $window -AutomationId "CorePathStatusText" -TimeoutSeconds $TimeoutSeconds
    $localizedCorePathStatus = Wait-Until -TimeoutSeconds $TimeoutSeconds -Message "Core path status did not localize after switching to Chinese." -Condition {
        $text = Get-ElementValue $localizedCorePathStatusText
        if ($text -eq "已配置") {
            return $text
        }
        return $null
    }
    $copySettingsFoldersButton = Get-ByAutomationId -Root $window -AutomationId "CopySettingsFoldersButton" -TimeoutSeconds $TimeoutSeconds
    Clear-SmokeClipboard
    Invoke-Element $copySettingsFoldersButton
    $localizedSettingsFoldersClipboard = Wait-Until -TimeoutSeconds $TimeoutSeconds -Message "Localized settings folders summary was not copied after switching to Chinese." -Condition {
        $text = Get-Clipboard -Raw -ErrorAction SilentlyContinue
        if ($text -match "应用数据:" -and
            $text -match "配置:" -and
            $text -match "日志:" -and
            $text -match "运行状态:" -and
            $text -match "内核状态: 已配置" -and
            $text -match [Regex]::Escape($AppHome)) {
            return $text
        }
        return $null
    }
    $saveSettingsButton = Get-ByAutomationId -Root $window -AutomationId "SaveSettingsButton" -TimeoutSeconds $TimeoutSeconds
    Invoke-Element $saveSettingsButton
    $appErrorText = Get-ByAutomationId -Root $window -AutomationId "AppErrorTextBlock" -TimeoutSeconds $TimeoutSeconds
    $languageSavedText = Wait-Until -TimeoutSeconds $TimeoutSeconds -Message "Saving Chinese language did not surface localized saved feedback." -Condition {
        $text = Get-ElementValue $appErrorText
        if ($text -match "设置已保存") {
            return $text
        }
        return $null
    }
    Write-Host "Language switched: $languageLabelText / $localizedThemeText / $localizedUpdateChannelText / $localizedProfileSummary / $localizedProfileState / $localizedProfileCopyText / $localizedSubscriptionSummary / $($localizedNewSubscriptionStatus.Split([Environment]::NewLine)[0]) / $($localizedSubscriptionSourceClipboard.Split([Environment]::NewLine)[0]) / $localizedSubscriptionSourceCopyText / $($localizedSubscriptionStatusClipboard.Split([Environment]::NewLine)[0]) / $localizedSubscriptionStatusCopyText / $localizedCorePathStatus / $($localizedSettingsFoldersClipboard.Split([Environment]::NewLine)[0]) / $languageSavedText"

    if ($process -and !$process.HasExited) {
        Stop-Process -Id $process.Id -Force
        Wait-Process -Id $process.Id -Timeout 5 -ErrorAction SilentlyContinue | Out-Null
    }
    $env:XTUNNEL_CLIENT_HOME = $AppHome
    $env:XTUNNEL_CLIENT_INSTANCE = "$InstanceName-language-restart"
    $process = Start-Process -FilePath (Resolve-Path $AppExe).Path -PassThru
    $env:XTUNNEL_CLIENT_HOME = $oldHome
    $env:XTUNNEL_CLIENT_INSTANCE = $oldInstance

    $window = Wait-Until -TimeoutSeconds $TimeoutSeconds -Message "Timed out waiting for language-persisted x-tunnel Client window." -Condition {
        $condition = [System.Windows.Automation.PropertyCondition]::new(
            [System.Windows.Automation.AutomationElement]::ProcessIdProperty,
            $process.Id)
        $root.FindFirst([System.Windows.Automation.TreeScope]::Children, $condition)
    }
    $settingsTab = Get-ByAutomationId -Root $window -AutomationId "SettingsTab" -TimeoutSeconds $TimeoutSeconds
    Select-Element $settingsTab
    $languageCombo = Get-ByAutomationId -Root $window -AutomationId "LanguageComboBox" -TimeoutSeconds $TimeoutSeconds
    $persistedLanguage = Wait-Until -TimeoutSeconds $TimeoutSeconds -Message "Chinese language did not persist after restart." -Condition {
        $text = Get-ElementValue $languageCombo
        if ($text -match "中文") {
            return $text
        }
        return $null
    }
    $languageLabel = Get-ByAutomationId -Root $window -AutomationId "SettingsLanguageLabel" -TimeoutSeconds $TimeoutSeconds
    $persistedLanguageLabel = Wait-Until -TimeoutSeconds $TimeoutSeconds -Message "Persisted Chinese UI label was not visible after restart." -Condition {
        $text = Get-ElementValue $languageLabel
        if ($text -eq "语言") {
            return $text
        }
        return $null
    }
    $themeCombo = Get-ByAutomationId -Root $window -AutomationId "ThemeComboBox" -TimeoutSeconds $TimeoutSeconds
    $persistedThemeText = Wait-Until -TimeoutSeconds $TimeoutSeconds -Message "Persisted Chinese theme option was not visible after restart." -Condition {
        $text = Get-ElementValue $themeCombo
        if ($text -eq "跟随系统") {
            return $text
        }
        return $null
    }
    Write-Host "Language persisted after restart: $persistedLanguage / $persistedLanguageLabel / $persistedThemeText"
    Write-Host "GUI smoke passed"
}
finally {
    $env:XTUNNEL_CLIENT_HOME = $oldHome
    $env:XTUNNEL_CLIENT_INSTANCE = $oldInstance
    if ($process -and !$process.HasExited) {
        Stop-Process -Id $process.Id -Force
    }
    Stop-ListenerJob -Job $serverJob -Port $port -RequestCount $serverRequestCount
    Stop-ListenerJob -Job $subscriptionJob -Port $subscriptionPort -RequestCount $subscriptionRequestCount
    if ($serverProcess -and !$serverProcess.HasExited) {
        Stop-Process -Id $serverProcess.Id -Force
    }
    if ($null -ne $oldClipboard) {
        try {
            Set-SmokeClipboardText -Value $oldClipboard
        }
        catch {
        }
    }
}
