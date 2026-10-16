[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$exe = Join-Path $repoRoot "src\DCx.Desktop\bin\Release\net10.0-windows10.0.19041.0\DCx.exe"
$fixtureDll = Join-Path $repoRoot "tests\DCx.WebFixtureServer\bin\Release\net10.0\DCx.WebFixtureServer.dll"
$runRoot = Join-Path $repoRoot "artifacts\test-results\p02-$([Guid]::NewGuid().ToString('N'))"
$fixturePort = 43188
$fixtureUrl = "http://127.0.0.1:$fixturePort/"
$previousDataRoot = $env:DCX_DATA_ROOT
$previousDownloadRoot = $env:DCX_DOWNLOAD_ROOT

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

function Wait-For([scriptblock]$condition, [string]$failureMessage, [int]$attempts = 40) {
    for ($attempt = 0; $attempt -lt $attempts; $attempt++) {
        $result = & $condition
        if ($result) { return $result }
        Start-Sleep -Milliseconds 500
    }
    throw $failureMessage
}

function Get-MainWindow([int]$processId) {
    $condition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ProcessIdProperty,
        $processId)
    return [System.Windows.Automation.AutomationElement]::RootElement.FindFirst(
        [System.Windows.Automation.TreeScope]::Children,
        $condition)
}

function Find-ById($root, [string]$automationId) {
    $condition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::AutomationIdProperty,
        $automationId)
    return $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
}

if (-not (Test-Path $exe) -or -not (Test-Path $fixtureDll)) {
    throw "Build DCx and the controlled fixture server before running P02 validation."
}

$env:DCX_DATA_ROOT = Join-Path $runRoot "data"
$env:DCX_DOWNLOAD_ROOT = Join-Path $runRoot "downloads"
$fixtureProcess = Start-Process "dotnet.exe" -ArgumentList @($fixtureDll, "--port", $fixturePort) -WindowStyle Hidden -PassThru
$process = $null
try {
    $null = Wait-For {
        try {
            (Invoke-WebRequest "${fixtureUrl}health" -UseBasicParsing -TimeoutSec 2).Content -eq "ok"
        }
        catch { $null }
    } "The controlled P02 fixture server did not start."

    $process = Start-Process $exe -PassThru
    $window = Wait-For { Get-MainWindow $process.Id } "The DCx main window did not appear."
    $address = Wait-For { Find-ById $window "AddressBox" } "The browser address field was not exposed."
    $null = Wait-For {
        $addressValue = $address.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
        $addressValue.Current.Value -eq "https://example.com/"
    } "The initial browser tab was not ready before the handoff test."
    $agentButton = Wait-For { Find-ById $window "AgentDrawerButton" } "The Agent button was not exposed."
    $agentButton.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()

    $target = Wait-For { Find-ById $window "AgentTargetBox" } "The controlled demo URL field was not exposed."
    $request = Find-ById $window "RequestAgentDemoButton"
    $target.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($fixtureUrl)
    $request.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()

    $approvalPanel = Wait-For { Find-ById $window "AgentApprovalPanel" } "The accessible agent approval group did not open."
    if ($approvalPanel.Current.Name -ne "Agent control approval required") {
        throw "The approval group has an unexpected accessible name."
    }
    $mode = Find-ById $window "AgentControlModeText"
    $null = Wait-For { $mode.Current.Name -like "Control: Approval pending.*" } "Control did not enter approval-pending mode."
    $approve = Find-ById $window "ApproveAgentButton"
    $approve.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()

    $null = Wait-For { $mode.Current.Name -like "Control: User takeover.*" } "The agent did not pause and return control for manual input."
    $instruction = Find-ById $window "AgentUserInstruction"
    if ($instruction.Current.Name -notlike "Your control.*") {
        throw "The manual-control instruction was not exposed as live accessible text."
    }

    $approvalInput = Wait-For {
        $nodes = $window.FindAll(
            [System.Windows.Automation.TreeScope]::Descendants,
            [System.Windows.Automation.Condition]::TrueCondition)
        @($nodes | Where-Object {
            $_.Current.ControlType -eq [System.Windows.Automation.ControlType]::Edit -and
            $_.Current.Name -eq "Approval code"
        }) | Select-Object -First 1
    } "The agent did not expose and focus the labeled user field."
    if (-not $approvalInput.Current.HasKeyboardFocus) {
        throw "Focus was not handed to the manual page field."
    }

    $approvalInput.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue("human fixture approval")
    $handBack = Find-ById $window "HandBackButton"
    if (-not $handBack.Current.IsEnabled) {
        throw "Hand back was not enabled after user takeover."
    }
    $handBack.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()

    $null = Wait-For { $mode.Current.Name -like "Control: User.*" } "The completed agent step did not restore user-control mode."
    $completionEvidence = Wait-For {
        $nodes = $window.FindAll(
            [System.Windows.Automation.TreeScope]::Descendants,
            [System.Windows.Automation.Condition]::TrueCondition)
        @($nodes | Where-Object { $_.Current.Name -eq "Agent resumed after user handback." }) | Select-Object -First 1
    } "The page did not expose the expected post-handback completion evidence."

    $restoredInput = Wait-For {
        $nodes = $window.FindAll(
            [System.Windows.Automation.TreeScope]::Descendants,
            [System.Windows.Automation.Condition]::TrueCondition)
        @($nodes | Where-Object {
            $_.Current.ControlType -eq [System.Windows.Automation.ControlType]::Edit -and
            $_.Current.Name -eq "Approval code" -and
            $_.Current.HasKeyboardFocus
        }) | Select-Object -First 1
    } "Focus was not restored to the user's page field after agent completion."

    $actionLog = Find-ById $window "AgentActionLog"
    $listItemCondition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::ListItem)
    $actionItems = $actionLog.FindAll(
        [System.Windows.Automation.TreeScope]::Descendants,
        $listItemCondition)
    $actionCount = $actionItems.Count
    if ($actionCount -lt 6) {
        throw "The accessible provenance log did not contain the expected handoff actions."
    }
    if (@($actionItems | Where-Object { $_.Current.Name -match "human fixture approval" }).Count -ne 0) {
        throw "The action log exposed the user's field value."
    }

    "Approval group=$($approvalPanel.Current.Name)"
    "Manual field=$($approvalInput.Current.Name)"
    "Manual value accepted=True"
    "Completion evidence=$($completionEvidence.Current.Name)"
    "Focus restored=$($restoredInput.Current.HasKeyboardFocus)"
    "Accessible action log entries=$actionCount"
    "User field value in action log=False"
    "Final mode=$($mode.Current.Name)"
    "P02 agent-user-agent handoff validation=PASS"
}
finally {
    if ($null -ne $process -and -not $process.HasExited) {
        $null = $process.CloseMainWindow()
        if (-not $process.WaitForExit(5000)) { $process.Kill() }
    }
    if ($null -ne $fixtureProcess -and -not $fixtureProcess.HasExited) {
        Stop-Process -Id $fixtureProcess.Id -Force
    }
    $env:DCX_DATA_ROOT = $previousDataRoot
    $env:DCX_DOWNLOAD_ROOT = $previousDownloadRoot
}
