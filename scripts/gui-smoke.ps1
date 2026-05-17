param(
    [string]$AppExe = (Join-Path $PSScriptRoot "..\src\XTunnelClient.App\bin\Debug\net8.0-windows\XTunnelClient.App.exe"),
    [string]$CoreRepo = (Join-Path $PSScriptRoot "..\..\x-tunnel"),
    [string]$CoreExe = (Join-Path $PSScriptRoot "..\..\x-tunnel\build\x-tunnel.exe"),
    [string]$AppHome = (Join-Path $env:TEMP ("xtunnel-client-gui-" + [Guid]::NewGuid().ToString("N"))),
    [string]$InstanceName = ("Local\x-tunnel-client-gui-" + [Guid]::NewGuid().ToString("N")),
    [string]$ScreenshotPath = (Join-Path $PSScriptRoot "..\artifacts\gui-smoke.png"),
    [string]$SubscriptionScreenshotPath = (Join-Path $PSScriptRoot "..\artifacts\gui-smoke-subscriptions.png"),
    [string]$DiagnosticsScreenshotPath = (Join-Path $PSScriptRoot "..\artifacts\gui-smoke-diagnostics.png"),
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
} -ArgumentList $port, 3

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
    $stateText = Get-ElementValue $statusState
    $profileText = Get-ElementValue $statusProfile
    if ($stateText -notmatch "Disconnected" -or $profileText -notmatch "Local x-tunnel") {
        throw "Unexpected status bar text: state='$stateText' profile='$profileText'"
    }

    $profilesTab = Get-ByAutomationId -Root $window -AutomationId "ProfilesTab" -TimeoutSeconds $TimeoutSeconds
    Select-Element $profilesTab

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

    $diagnosticsTab = Get-ByAutomationId -Root $window -AutomationId "DiagnosticsTab" -TimeoutSeconds $TimeoutSeconds
    Select-Element $diagnosticsTab

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

    $testButton = Get-ByAutomationId -Root $window -AutomationId "TestNetworkButton" -TimeoutSeconds $TimeoutSeconds
    Invoke-Element $testButton

    $resultBox = Get-ByAutomationId -Root $window -AutomationId "NetworkTestResultTextBox" -TimeoutSeconds $TimeoutSeconds
    $resultText = Wait-Until -TimeoutSeconds $TimeoutSeconds -Message "Network test did not report success." -Condition {
        $text = Get-ElementValue $resultBox
        if ($text -match "Direct: ok" -and $text -match "status=204") {
            return $text
        }
        return $null
    }

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

    $endpointButton = Get-ByAutomationId -Root $window -AutomationId "TestProfileEndpointButton" -TimeoutSeconds $TimeoutSeconds
    Invoke-Element $endpointButton

    $endpointResultBox = Get-ByAutomationId -Root $window -AutomationId "ProfileEndpointTestResultTextBox" -TimeoutSeconds $TimeoutSeconds
    $endpointText = Wait-Until -TimeoutSeconds $TimeoutSeconds -Message "Profile endpoint test did not report success." -Condition {
        $text = Get-ElementValue $endpointResultBox
        if ($text -match "Forward TCP: ok" -and $text -match [Regex]::Escape($coreForwardUrl.Replace("/tunnel", ""))) {
            return $text
        }
        return $null
    }

    Write-Host $resultText
    Write-Host $endpointText
    Save-ElementScreenshot -Element $window -Path $DiagnosticsScreenshotPath
    Write-Host "Diagnostics GUI screenshot: $DiagnosticsScreenshotPath"

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

    Select-Element $diagnosticsTab
    $testButton = Get-ByAutomationId -Root $window -AutomationId "TestNetworkButton" -TimeoutSeconds $TimeoutSeconds
    Invoke-Element $testButton
    $connectedNetworkText = Wait-Until -TimeoutSeconds $TimeoutSeconds -Message "Connected network test did not report proxy success." -Condition {
        $text = Get-ElementValue $resultBox
        if ($text -match "Direct: ok" -and $text -match "HTTP proxy: ok" -and $text -match "status=204") {
            return $text
        }
        return $null
    }
    Write-Host "Connected network test: $connectedNetworkText"

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
    Set-ElementValue -Element $logFilterBox -Value "unlikely-smoke-filter"
    Wait-Until -TimeoutSeconds $TimeoutSeconds -Message "Log text filter did not narrow the output." -Condition {
        $text = Get-ElementValue $filteredLogBox
        if ([string]::IsNullOrWhiteSpace($text)) {
            return "filtered"
        }
        return $null
    } | Out-Null
    $clearLogFiltersButton = Get-ByAutomationId -Root $window -AutomationId "ClearLogFiltersButton" -TimeoutSeconds $TimeoutSeconds
    Invoke-Element $clearLogFiltersButton
    Wait-Until -TimeoutSeconds $TimeoutSeconds -Message "Clear log filters did not restore log output." -Condition {
        $text = Get-ElementValue $filteredLogBox
        if (![string]::IsNullOrWhiteSpace($text)) {
            return $text
        }
        return $null
    } | Out-Null

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
