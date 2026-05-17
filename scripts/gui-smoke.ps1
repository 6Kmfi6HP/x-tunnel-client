param(
    [string]$AppExe = (Join-Path $PSScriptRoot "..\src\XTunnelClient.App\bin\Debug\net8.0-windows\XTunnelClient.App.exe"),
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

$port = Get-FreeTcpPort
$forwardPort = Get-FreeTcpPort
$testUrl = "http://127.0.0.1:$port/generate_204"
$forwardUrl = "ws://127.0.0.1:$forwardPort/tunnel"
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

$forwardJob = Start-Job -ScriptBlock {
    param([int]$Port)
    $listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, $Port)
    $listener.Start()
    try {
        $client = $listener.AcceptTcpClient()
        $client.Dispose()
    }
    finally {
        $listener.Stop()
    }
} -ArgumentList $forwardPort

$oldHome = $env:XTUNNEL_CLIENT_HOME
$oldInstance = $env:XTUNNEL_CLIENT_INSTANCE
$process = $null
try {
    New-Item -ItemType Directory -Force -Path $AppHome | Out-Null
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

    $profilesTab = Get-ByAutomationId -Root $window -AutomationId "ProfilesTab" -TimeoutSeconds $TimeoutSeconds
    Select-Element $profilesTab

    $forwardBox = Get-ByAutomationId -Root $window -AutomationId "ProfileForwardTextBox" -TimeoutSeconds $TimeoutSeconds
    Set-ElementValue -Element $forwardBox -Value $forwardUrl

    $applyProfileButton = Get-ByAutomationId -Root $window -AutomationId "ApplyProfileFormButton" -TimeoutSeconds $TimeoutSeconds
    Invoke-Element $applyProfileButton

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
        if ($text -match "Forward TCP: ok" -and $text -match [Regex]::Escape($forwardUrl.Replace("/tunnel", ""))) {
            return $text
        }
        return $null
    }

    Write-Host "GUI smoke passed"
    Write-Host $resultText
    Write-Host $endpointText

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
    if ($forwardJob) {
        Stop-Job $forwardJob -ErrorAction SilentlyContinue | Out-Null
        Remove-Job $forwardJob -Force -ErrorAction SilentlyContinue
    }
}
