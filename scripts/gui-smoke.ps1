param(
    [string]$AppExe = (Join-Path $PSScriptRoot "..\src\XTunnelClient.App\bin\Debug\net8.0-windows\XTunnelClient.App.exe"),
    [string]$CoreRepo = (Join-Path $PSScriptRoot "..\..\x-tunnel"),
    [string]$CoreExe = (Join-Path $PSScriptRoot "..\..\x-tunnel\build\x-tunnel.exe"),
    [string]$AppHome = (Join-Path $env:TEMP ("xtunnel-client-gui-" + [Guid]::NewGuid().ToString("N"))),
    [string]$InstanceName = ("Local\x-tunnel-client-gui-" + [Guid]::NewGuid().ToString("N")),
    [string]$OverviewScreenshotPath = (Join-Path $PSScriptRoot "..\artifacts\gui-smoke-overview.png"),
    [string]$OverviewNetworkScreenshotPath = (Join-Path $PSScriptRoot "..\artifacts\gui-smoke-overview-network.png"),
    [string]$ScreenshotPath = (Join-Path $PSScriptRoot "..\artifacts\gui-smoke.png"),
    [string]$SubscriptionScreenshotPath = (Join-Path $PSScriptRoot "..\artifacts\gui-smoke-subscriptions.png"),
    [string]$DiagnosticsScreenshotPath = (Join-Path $PSScriptRoot "..\artifacts\gui-smoke-diagnostics.png"),
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
} -ArgumentList $port, 6

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
} -ArgumentList $subscriptionPort, 2

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
    @"
{
  "listen": "ws://127.0.0.1:$coreServerPort/tunnel",
  "token": "smoke-token",
  "cidr": "127.0.0.1/32",
  "allow-target": "127.0.0.0/8",
  "fallback": true,
  "shutdown_timeout": "2s"
}
"@ | Set-Content -Encoding UTF8 $serverConfig
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
    $statusCore = Get-ByAutomationId -Root $window -AutomationId "StatusBarCoreText" -TimeoutSeconds $TimeoutSeconds
    $statusIssue = Get-ByAutomationId -Root $window -AutomationId "StatusBarIssueText" -TimeoutSeconds $TimeoutSeconds
    $stateText = Get-ElementValue $statusState
    $profileText = Get-ElementValue $statusProfile
    if ($stateText -notmatch "Disconnected" -or $profileText -notmatch "Local x-tunnel") {
        throw "Unexpected status bar text: state='$stateText' profile='$profileText'"
    }
    $statusProxyModeText = Get-ElementValue $statusProxyMode
    $statusLocalProxyText = Get-ElementValue $statusLocalProxy
    $statusCoreText = Get-ElementValue $statusCore
    $statusIssueText = Get-ElementValue $statusIssue
    if ($statusProxyModeText -notmatch "Off" -or $statusLocalProxyText -notmatch "HTTP 127.0.0.1:" -or $statusCoreText -notmatch "Core not running") {
        throw "Unexpected status bar details: proxy='$statusProxyModeText' local='$statusLocalProxyText' core='$statusCoreText' issue='$statusIssueText'"
    }
    Write-Host "Status bar details: $statusProxyModeText / $statusLocalProxyText / $statusCoreText / $statusIssueText"

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

    Save-ElementScreenshot -Element $window -Path $OverviewScreenshotPath
    Write-Host "Overview GUI screenshot: $OverviewScreenshotPath"

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

    $copyProfileSummaryButton = Get-ByAutomationId -Root $window -AutomationId "CopyProfileSummaryButton" -TimeoutSeconds $TimeoutSeconds
    Set-Clipboard -Value ""
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

    $testVisibleProfilesButton = Get-ByAutomationId -Root $window -AutomationId "TestVisibleProfilesButton" -TimeoutSeconds $TimeoutSeconds
    Invoke-Element $testVisibleProfilesButton
    $profileBatchTestTextBlock = Get-ByAutomationId -Root $window -AutomationId "ProfileBatchTestTextBlock" -TimeoutSeconds $TimeoutSeconds
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
    Set-Clipboard -Value ""
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
    Select-ComboBoxItem -Element $targetCombo -Name "Cloudflare Trace" -TimeoutSeconds $TimeoutSeconds
    $urlBox = Get-ByAutomationId -Root $window -AutomationId "NetworkTestUrlTextBox" -TimeoutSeconds $TimeoutSeconds
    $presetUrl = Wait-Until -TimeoutSeconds $TimeoutSeconds -Message "Network target preset did not update the test URL." -Condition {
        $text = Get-ElementValue $urlBox
        if ($text -match "cloudflare.com/cdn-cgi/trace") {
            return $text
        }
        return $null
    }
    Write-Host "Network target preset URL: $presetUrl"
    Set-ElementValue -Element $urlBox -Value $testUrl

    $overviewTab = Get-ByAutomationId -Root $window -AutomationId "OverviewTab" -TimeoutSeconds $TimeoutSeconds
    Select-Element $overviewTab
    $copyProxyAddressButton = Get-ByAutomationId -Root $window -AutomationId "CopyProxyAddressButton" -TimeoutSeconds $TimeoutSeconds
    Set-Clipboard -Value ""
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
    Set-Clipboard -Value ""
    Invoke-Element $copyOverviewStatusButton
    $clipboardOverviewStatus = Wait-Until -TimeoutSeconds $TimeoutSeconds -Message "Copy overview status did not place the summary on the clipboard." -Condition {
        $text = Get-Clipboard -Raw -ErrorAction SilentlyContinue
        if ($text -match "Profile: Local x-tunnel" -and $text -match "Network: Direct ok" -and $text -match "Network updated: Last tested" -and $text -match "Local proxy: HTTP") {
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
    Set-Clipboard -Value ""
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
    Set-Clipboard -Value ""
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
    Set-Clipboard -Value ""
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
    Set-Clipboard -Value ""
    Invoke-Element $copyDiagnosticsReportButton
    $clipboardDiagnosticsReport = Wait-Until -TimeoutSeconds $TimeoutSeconds -Message "Copy diagnostics report did not place the JSON report on the clipboard." -Condition {
        $text = Get-Clipboard -Raw -ErrorAction SilentlyContinue
        if ($text -match '"createdAt"' -and $text -match '"activeProfile"' -and $text -match '"portChecks"' -and $text -match "Local x-tunnel") {
            return $text
        }
        return $null
    }
    Write-Host "Copied diagnostics report: $($clipboardDiagnosticsReport.Split([Environment]::NewLine)[0])"
    Save-ElementScreenshot -Element $window -Path $DiagnosticsScreenshotPath
    Write-Host "Diagnostics GUI screenshot: $DiagnosticsScreenshotPath"

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
    Write-Host "Connected status bar: $connectedCoreStatus / $connectedLocalProxy"

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
    $copyCorePathButton = Get-ByAutomationId -Root $window -AutomationId "CopyCorePathButton" -TimeoutSeconds $TimeoutSeconds
    Set-Clipboard -Value ""
    Invoke-Element $copyCorePathButton
    $clipboardCorePath = Wait-Until -TimeoutSeconds $TimeoutSeconds -Message "Copy core path did not place x-tunnel.exe on the clipboard." -Condition {
        $text = Get-Clipboard -Raw -ErrorAction SilentlyContinue
        if ($text -match [Regex]::Escape($settingsCorePath)) {
            return $text
        }
        return $null
    }
    Write-Host "Copied core path: $clipboardCorePath"
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
    Set-Clipboard -Value ""
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

    $subscriptionsTab = Get-ByAutomationId -Root $window -AutomationId "SubscriptionsTab" -TimeoutSeconds $TimeoutSeconds
    Select-Element $subscriptionsTab

    $newSubscriptionButton = Get-ByAutomationId -Root $window -AutomationId "NewSubscriptionButton" -TimeoutSeconds $TimeoutSeconds
    Invoke-Element $newSubscriptionButton

    $subscriptionNameBox = Get-ByAutomationId -Root $window -AutomationId "SubscriptionNameTextBox" -TimeoutSeconds $TimeoutSeconds
    Set-ElementValue -Element $subscriptionNameBox -Value "Smoke subscription"

    $subscriptionUrlBox = Get-ByAutomationId -Root $window -AutomationId "SubscriptionUrlTextBox" -TimeoutSeconds $TimeoutSeconds
    Set-ElementValue -Element $subscriptionUrlBox -Value $subscriptionUrl

    $updateSubscriptionButton = Get-ByAutomationId -Root $window -AutomationId "UpdateSubscriptionNowButton" -TimeoutSeconds $TimeoutSeconds
    Invoke-Element $updateSubscriptionButton

    $subscriptionStatusBox = Get-ByAutomationId -Root $window -AutomationId "SubscriptionStatusTextBox" -TimeoutSeconds $TimeoutSeconds
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
    $copySubscriptionStatusButton = Get-ByAutomationId -Root $window -AutomationId "CopySubscriptionStatusButton" -TimeoutSeconds $TimeoutSeconds
    Set-Clipboard -Value ""
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

    $appErrorText = Get-ByAutomationId -Root $window -AutomationId "AppErrorTextBlock" -TimeoutSeconds $TimeoutSeconds
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

    Save-ElementScreenshot -Element $window -Path $ScreenshotPath
    Write-Host "GUI screenshot: $ScreenshotPath"
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
    Write-Host "GUI smoke passed"
}
finally {
    $env:XTUNNEL_CLIENT_HOME = $oldHome
    $env:XTUNNEL_CLIENT_INSTANCE = $oldInstance
    if ($process -and !$process.HasExited) {
        Stop-Process -Id $process.Id -Force
    }
    if ($serverJob) {
        Stop-Job $serverJob -ErrorAction SilentlyContinue | Out-Null
        Remove-Job $serverJob -Force -ErrorAction SilentlyContinue
    }
    if ($subscriptionJob) {
        Stop-Job $subscriptionJob -ErrorAction SilentlyContinue | Out-Null
        Remove-Job $subscriptionJob -Force -ErrorAction SilentlyContinue
    }
    if ($serverProcess -and !$serverProcess.HasExited) {
        Stop-Process -Id $serverProcess.Id -Force
    }
    if ($null -ne $oldClipboard) {
        try {
            Set-Clipboard -Value $oldClipboard
        }
        catch {
        }
    }
}
