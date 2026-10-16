[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$exe = Join-Path $repoRoot "src\DCx.Desktop\bin\Release\net10.0-windows10.0.19041.0\DCx.exe"
$previousRepoRoot = $env:DCX_REPO_ROOT
$previousDataRoot = $env:DCX_DATA_ROOT
$runRoot = Join-Path $repoRoot "artifacts\test-results\p03-$([Guid]::NewGuid().ToString('N'))"

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

function Find-WindowByProcessAndId([int]$processId, [string]$automationId) {
    $windows = [System.Windows.Automation.AutomationElement]::RootElement.FindAll(
        [System.Windows.Automation.TreeScope]::Descendants,
        [System.Windows.Automation.Condition]::TrueCondition)
    return @($windows | Where-Object {
        $_.Current.ProcessId -eq $processId -and $_.Current.AutomationId -eq $automationId
    }) | Select-Object -First 1
}

function Find-ById($root, [string]$automationId) {
    $condition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::AutomationIdProperty,
        $automationId)
    return $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
}

if (-not (Test-Path $exe)) {
    throw "Build DCx before running P03 validation."
}

$env:DCX_REPO_ROOT = $repoRoot
$env:DCX_DATA_ROOT = Join-Path $runRoot "data"
$process = Start-Process $exe -PassThru
$fixtureProcessId = $null
try {
    $mainWindow = Wait-For {
        $condition = New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::ProcessIdProperty,
            $process.Id)
        [System.Windows.Automation.AutomationElement]::RootElement.FindFirst(
            [System.Windows.Automation.TreeScope]::Children,
            $condition)
    } "The DCx main window did not appear."
    $address = Wait-For { Find-ById $mainWindow "AddressBox" } "The browser shell was not ready."
    $null = Wait-For {
        $value = $address.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
        $value.Current.Value -eq "https://example.com/"
    } "The DCx shell did not finish startup."

    $windowsAppButton = Find-ById $mainWindow "WindowsAppButton"
    $windowsAppButton.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    $controlWindow = Wait-For {
        Find-WindowByProcessAndId $process.Id "NativeAppControlWindow"
    } "The accessible Windows application control window did not open."

    $inspect = Find-ById $controlWindow "InspectReadmeButton"
    $inspect.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    $instruction = Find-ById $controlWindow "ExternalInstructionText"
    $null = Wait-For {
        $instruction.Current.Name -like "Read-only PowerShell inspection completed.*"
    } "The restricted read-only PowerShell inspection did not complete."

    $launch = Find-ById $controlWindow "LaunchFixtureButton"
    $launch.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    $fixtureWindow = Wait-For {
        $windows = [System.Windows.Automation.AutomationElement]::RootElement.FindAll(
            [System.Windows.Automation.TreeScope]::Children,
            [System.Windows.Automation.Condition]::TrueCondition)
        @($windows | Where-Object { $_.Current.AutomationId -eq "NativeFixtureWindow" }) | Select-Object -First 1
    } "The broker-launched controlled native application did not appear."
    $fixtureProcessId = $fixtureWindow.Current.ProcessId
    if ($fixtureProcessId -eq $process.Id) {
        throw "The controlled fixture did not launch as a distinct Windows process."
    }

    $approvalPanel = Wait-For { Find-ById $controlWindow "UiaApprovalPanel" } "The UI Automation approval group did not open."
    $approve = Find-ById $controlWindow "ApproveUiaButton"
    $approve.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    $mode = Find-ById $controlWindow "ExternalControlModeText"
    $null = Wait-For { $mode.Current.Name -like "Control: User takeover.*" } "The agent did not yield the native app to the user."

    $fixtureInput = Wait-For { Find-ById $fixtureWindow "FixtureInput" } "The fixture Shared text field was not accessible."
    $fixtureValue = $fixtureInput.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
    if ($fixtureValue.Current.Value -ne "Agent prepared fixture text") {
        throw "The approved agent UIA action did not prepare the fixture field."
    }
    if (-not $fixtureInput.Current.HasKeyboardFocus) {
        throw "The external app field did not receive focus for user takeover."
    }

    $fixtureValue.SetValue("Human controlled fixture value")
    $resume = Find-ById $controlWindow "ResumeExternalAgentButton"
    if (-not $resume.Current.IsEnabled) {
        throw "Resume agent was not enabled after user takeover."
    }
    $resume.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    $null = Wait-For { $mode.Current.Name -like "Control: User.*" } "The resumed agent did not return native-app control to the user."

    $fixtureWindow = Wait-For {
        $windows = [System.Windows.Automation.AutomationElement]::RootElement.FindAll(
            [System.Windows.Automation.TreeScope]::Children,
            [System.Windows.Automation.Condition]::TrueCondition)
        @($windows | Where-Object { $_.Current.ProcessId -eq $fixtureProcessId }) | Select-Object -First 1
    } "The controlled app disappeared during handoff."
    $fixtureInput = Find-ById $fixtureWindow "FixtureInput"
    $finalValue = $fixtureInput.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value
    if ($finalValue -ne "Human controlled fixture value") {
        throw "The resumed agent overwrote the user's native-app value."
    }
    if (-not $fixtureInput.Current.HasKeyboardFocus) {
        throw "Native-app focus was not restored after agent resume."
    }

    $log = Find-ById $controlWindow "ExternalActionLog"
    $listItemCondition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::ListItem)
    $logItems = $log.FindAll([System.Windows.Automation.TreeScope]::Descendants, $listItemCondition)
    if ($logItems.Count -lt 5) {
        throw "The accessible broker/handoff log is incomplete."
    }
    if (@($logItems | Where-Object { $_.Current.Name -match "Human controlled fixture value" }).Count -ne 0) {
        throw "The broker log exposed the user's field value."
    }

    $closeFixture = Find-ById $controlWindow "CloseFixtureButton"
    $closeFixture.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    $null = Wait-For {
        try {
            $candidate = Get-Process -Id $fixtureProcessId -ErrorAction Stop
            $candidate.HasExited
        }
        catch { $true }
    } "The explicitly requested controlled-app close did not complete."
    $fixtureProcessId = $null

    "Restricted PowerShell inspection=PASS"
    "External process distinct=True"
    "Scoped UIA approval=$($approvalPanel.Current.Name)"
    "Agent prepared field=True"
    "User takeover focus=True"
    "User value preserved=True"
    "Resume focus restored=True"
    "User value in broker log=False"
    "Accessible broker log entries=$($logItems.Count)"
    "Explicit controlled-app close=PASS"
    "P03 native application handoff validation=PASS"
}
finally {
    if ($null -ne $fixtureProcessId) {
        Stop-Process -Id $fixtureProcessId -Force -ErrorAction SilentlyContinue
    }
    if (-not $process.HasExited) {
        $null = $process.CloseMainWindow()
        if (-not $process.WaitForExit(5000)) { $process.Kill() }
    }
    $env:DCX_REPO_ROOT = $previousRepoRoot
    $env:DCX_DATA_ROOT = $previousDataRoot
}
