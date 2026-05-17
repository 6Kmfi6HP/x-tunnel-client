param(
    [string]$AppExe = (Join-Path $PSScriptRoot "..\src\XTunnelClient.App\bin\Debug\net8.0-windows\XTunnelClient.App.exe"),
    [string]$CoreRepo = (Join-Path $PSScriptRoot "..\..\x-tunnel"),
    [string]$CoreExe = (Join-Path $PSScriptRoot "..\..\x-tunnel\build\x-tunnel.exe"),
    [string]$AppHome = (Join-Path $env:TEMP ("xtunnel-client-gui-" + [Guid]::NewGuid().ToString("N"))),
    [string]$InstanceName = ("Local\x-tunnel-client-gui-" + [Guid]::NewGuid().ToString("N")),
    [int]$TimeoutSeconds = 25
)

$ErrorActionPreference = "Stop"

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

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
    $pattern = $Element.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
    $pattern.Invoke()
}

function Select-Element {
    param([System.Windows.Automation.AutomationElement]$Element)
    $pattern = $Element.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)
    $pattern.Select()
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
    param([int]$Port)
    $listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, $Port)
    $listener.Start()
    try {
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
    finally {
        $listener.Stop()
    }
} -ArgumentList $port

$subscriptionJob = Start-Job -ScriptBlock {
    param([int]$Port)
    $body = '[{"name":"Smoke subscription profile","core_config":{"listen":"socks5://127.0.0.1:12080","forward":"ws://127.0.0.1:18080/tunnel","token_ref":"secret:profile-token","connections":1}}]'
    $listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, $Port)
    $listener.Start()
    try {
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
    finally {
        $listener.Stop()
    }
} -ArgumentList $subscriptionPort

$oldHome = $env:XTUNNEL_CLIENT_HOME
$oldInstance = $env:XTUNNEL_CLIENT_INSTANCE
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

    $diagnosticsTab = Get-ByAutomationId -Root $window -AutomationId "DiagnosticsTab" -TimeoutSeconds $TimeoutSeconds
    Select-Element $diagnosticsTab

    $urlBox = Get-ByAutomationId -Root $window -AutomationId "NetworkTestUrlTextBox" -TimeoutSeconds $TimeoutSeconds
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

    Select-Element $profilesTab
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
}
