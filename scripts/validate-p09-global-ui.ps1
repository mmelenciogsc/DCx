[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$RunRoot
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$runRoot = [IO.Path]::GetFullPath($RunRoot)
$allowedRoot = [IO.Path]::GetFullPath((Join-Path $repoRoot "artifacts\test-results"))
if (-not $runRoot.StartsWith($allowedRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw "The P09 global UIA run root must be beneath the repository test-results directory."
}

$desktop = Join-Path $repoRoot "src\DCx.Desktop\bin\Release\net10.0-windows10.0.19041.0\DCx.exe"
$fixtureDataRoot = Join-Path $runRoot "global-ui-fixture-data"
$shellDataRoot = Join-Path $runRoot "global-ui-shell-data"
$evidencePath = Join-Path $runRoot "global-knowledge-uia.json"
$fixtureProcess = $null
$shellProcess = $null
$previousDataRoot = $env:DCX_DATA_ROOT
$previousFixture = $env:DCX_GLOBAL_KNOWLEDGE_FIXTURE
$previousFixtureRoot = $env:DCX_GLOBAL_KNOWLEDGE_FIXTURE_ROOT
$previousOffline = $env:DCX_OFFLINE_STARTUP

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

function Wait-For([scriptblock]$Condition, [string]$FailureMessage, [int]$Attempts = 100) {
    for ($attempt = 0; $attempt -lt $Attempts; $attempt++) {
        $result = & $Condition
        if ($result) { return $result }
        Start-Sleep -Milliseconds 200
    }
    throw $FailureMessage
}

function Find-ById($Root, [string]$AutomationId) {
    $condition = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::AutomationIdProperty,
        $AutomationId)
    return $Root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
}

function Find-WindowByProcessAndId([int]$ProcessId, [string]$AutomationId) {
    $elements = [System.Windows.Automation.AutomationElement]::RootElement.FindAll(
        [System.Windows.Automation.TreeScope]::Descendants,
        [System.Windows.Automation.Condition]::TrueCondition)
    return @($elements | Where-Object {
        $_.Current.ProcessId -eq $ProcessId -and $_.Current.AutomationId -eq $AutomationId
    }) | Select-Object -First 1
}

function Invoke-ById($Root, [string]$AutomationId) {
    $element = Wait-For { Find-ById $Root $AutomationId } "The $AutomationId action was not exposed."
    $element.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    return $element
}

function Set-Value($Root, [string]$AutomationId, [string]$Value) {
    $element = Wait-For { Find-ById $Root $AutomationId } "The $AutomationId field was not exposed."
    $element.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($Value)
}

function Get-Value($Root, [string]$AutomationId) {
    $element = Wait-For { Find-ById $Root $AutomationId } "The $AutomationId value was not exposed."
    return $element.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value
}

function Stop-Gracefully($Process) {
    if ($null -eq $Process) { return }
    try {
        if (-not $Process.HasExited) {
            [void]$Process.CloseMainWindow()
            if (-not $Process.WaitForExit(5000)) {
                Stop-Process -Id $Process.Id -Force -ErrorAction SilentlyContinue
            }
        }
    }
    catch {
        Stop-Process -Id $Process.Id -Force -ErrorAction SilentlyContinue
    }
}

if (-not (Test-Path -LiteralPath $desktop -PathType Leaf)) {
    throw "Build DCx Desktop before running P09 global UIA validation."
}

New-Item -ItemType Directory -Path $runRoot -Force | Out-Null
try {
    $env:DCX_DATA_ROOT = $fixtureDataRoot
    $env:DCX_GLOBAL_KNOWLEDGE_FIXTURE = "1"
    $env:DCX_GLOBAL_KNOWLEDGE_FIXTURE_ROOT = $runRoot
    $env:DCX_OFFLINE_STARTUP = "1"
    $fixtureProcess = Start-Process $desktop -ArgumentList "--global-knowledge-fixture" -PassThru
    $fixtureWindow = Wait-For {
        Find-WindowByProcessAndId $fixtureProcess.Id "GlobalKnowledgeWindow"
    } "The global knowledge fixture window did not appear."

    $heading = Wait-For {
        $item = Find-ById $fixtureWindow "GlobalKnowledgeHeading"
        if ($null -ne $item -and $item.Current.HasKeyboardFocus) { $item }
    } "Initial keyboard focus did not land on the global knowledge heading."
    $boundary = Find-ById $fixtureWindow "GlobalKnowledgeBoundary"
    if ($null -eq $boundary -or $boundary.Current.Name -notlike "*synthetic*Network requests*private-knowledge access*unavailable*") {
        throw "The fixture boundary was not exposed accessibly."
    }

    $initialSummary = Wait-For {
        $value = Get-Value $fixtureWindow "GlobalKnowledgeSummary"
        if ($value -like "*Active signed release: 0.*Resources: 0.*Synchronization available: yes, by explicit action.*") { $value }
    } "The empty-but-configured signed replica state was not exposed."
    $idleRefresh = Wait-For {
        $item = Find-ById $fixtureWindow "RefreshGlobalKnowledgeButton"
        if ($null -ne $item -and $item.Current.IsEnabled) { $item }
    } "The global knowledge window did not return to an idle state."

    $actionIds = @(
        "RefreshGlobalKnowledgeButton",
        "SynchronizeGlobalKnowledgeButton",
        "CancelGlobalKnowledgeButton",
        "SearchGlobalKnowledgeButton")
    $accessKeys = @()
    foreach ($automationId in $actionIds) {
        $action = Wait-For {
            $candidate = Find-ById $fixtureWindow $automationId
            if ($null -ne $candidate -and
                -not [string]::IsNullOrWhiteSpace($candidate.Current.AccessKey) -and
                ($automationId -eq "CancelGlobalKnowledgeButton" -or
                 $candidate.Current.IsEnabled)) { $candidate }
        } "The $automationId action lacks its required access key or enabled state."
        $accessKeys += $action.Current.AccessKey
    }
    if (@($accessKeys | Sort-Object -Unique).Count -ne $accessKeys.Count) {
        throw "Global knowledge actions expose duplicate access keys."
    }

    $synchronize = Find-ById $fixtureWindow "SynchronizeGlobalKnowledgeButton"
    if (-not $synchronize.Current.IsEnabled) {
        throw "Explicit synchronization was not enabled for the configured fixture."
    }
    $synchronize.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    $cancelDuringSync = Wait-For {
        $item = Find-ById $fixtureWindow "CancelGlobalKnowledgeButton"
        if ($null -ne $item -and $item.Current.IsEnabled -and $item.Current.IsKeyboardFocusable -and
            -not [string]::IsNullOrWhiteSpace($item.Current.AccessKey)) { $item }
    } "Cancel was not keyboard accessible while synchronization was active."
    $syncedStatus = Wait-For {
        $item = Find-ById $fixtureWindow "GlobalKnowledgeStatus"
        if ($null -ne $item -and $item.Current.Name -like "Applied 1 signed releases.*active release is 1.*") { $item.Current.Name }
    } "The signed release was not synchronized through the accessible action."
    $syncedSummary = Wait-For {
        $value = Get-Value $fixtureWindow "GlobalKnowledgeSummary"
        if ($value -like "*Active signed release: 1.*Resources: 2.*") { $value }
    } "The activated signed replica state was not exposed."

    Set-Value $fixtureWindow "GlobalKnowledgeQuery" "deterministic capability"
    Invoke-ById $fixtureWindow "SearchGlobalKnowledgeButton" | Out-Null
    $searchStatus = Wait-For {
        $item = Find-ById $fixtureWindow "GlobalKnowledgeStatus"
        if ($null -ne $item -and $item.Current.Name -like
            "Offline search complete. Results: 1. Network requests: 0. Private collections searched: 0.*") { $item.Current.Name }
    } "The offline/private-isolation status was not announced."
    $results = Find-ById $fixtureWindow "GlobalKnowledgeResults"
    $listItemCondition = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::ListItem)
    $items = $results.FindAll([System.Windows.Automation.TreeScope]::Descendants, $listItemCondition)
    $itemNames = @($items | ForEach-Object { $_.Current.Name })
    if ($items.Count -ne 1 -or $items[0].Current.Name -ne
        "Focus restoration validator. Ccb. Version 2.1.0. Available.") {
        throw "The expected CCB was not returned as the sole offline result. Count=$($items.Count); names=$($itemNames -join ' | ')."
    }
    $resultItem = $items[0]
    $resultItem.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    $details = Wait-For {
        $value = Get-Value $fixtureWindow "GlobalKnowledgeResultDetails"
        if ($value -like "*Kind: Ccb.*Resource ID: global:focus-validator.*License: MIT.*Artifact: none.*") { $value }
    } "The selected signed-catalog metadata was not exposed."
    $connections = @(Get-NetTCPConnection -OwningProcess $fixtureProcess.Id -ErrorAction SilentlyContinue |
        Where-Object { $_.State -in @("Established", "Listen", "SynSent", "SynReceived") })
    if ($connections.Count -ne 0) {
        throw "The in-memory global UI fixture unexpectedly opened a network socket."
    }

    Stop-Gracefully $fixtureProcess
    $fixtureProcess = $null

    $env:DCX_DATA_ROOT = $shellDataRoot
    $env:DCX_GLOBAL_KNOWLEDGE_FIXTURE = $null
    $env:DCX_GLOBAL_KNOWLEDGE_FIXTURE_ROOT = $null
    $shellProcess = Start-Process $desktop -PassThru
    $shellWindow = Wait-For {
        $condition = [System.Windows.Automation.PropertyCondition]::new(
            [System.Windows.Automation.AutomationElement]::ProcessIdProperty,
            $shellProcess.Id)
        [System.Windows.Automation.AutomationElement]::RootElement.FindFirst(
            [System.Windows.Automation.TreeScope]::Children,
            $condition)
    } "The main DCx window did not appear."
    $shellButton = Invoke-ById $shellWindow "GlobalKnowledgeButton"
    if (-not $shellButton.Current.IsKeyboardFocusable) {
        throw "The main-shell global knowledge entry is not keyboard focusable."
    }
    $unavailableWindow = Wait-For {
        Find-WindowByProcessAndId $shellProcess.Id "GlobalKnowledgeWindow"
    } "The main-shell global knowledge window did not open."
    $unavailableStatus = Wait-For {
        $item = Find-ById $unavailableWindow "GlobalKnowledgeStatus"
        if ($null -ne $item -and $item.Current.Name -like
            "Global knowledge is not configured.*No remote state*empty catalog*synchronization success*inferred.*") { $item.Current.Name }
    } "The unconfigured product state did not fail closed accessibly."
    if ((Find-ById $unavailableWindow "SynchronizeGlobalKnowledgeButton").Current.IsEnabled -or
        (Find-ById $unavailableWindow "SearchGlobalKnowledgeButton").Current.IsEnabled) {
        throw "Search or synchronization was enabled without a configured trust root and registry."
    }

    [ordered]@{
        schemaVersion = 1
        fixtureKind = "signed-in-memory-global-replica"
        initialRelease = 0
        synchronizedRelease = 1
        synchronizedResources = 2
        offlineSearchResults = 1
        selectedResourceId = "global:focus-validator"
        networkSockets = 0
        privateCollectionsSearched = 0
        actionAccessKeys = $accessKeys
        shellEntry = "pass"
        unconfiguredFailClosed = "pass"
        productionActions = 0
    } | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $evidencePath -Encoding utf8

    "P09 global knowledge UIA=PASS"
    "P09 explicit signed synchronization UIA=PASS"
    "P09 offline global CKB/CCB search UIA=PASS"
    "P09 private-collection and network isolation UIA=PASS"
    "P09 unconfigured default fail-closed UIA=PASS"
    "P09 global UIA evidence=$evidencePath"
}
finally {
    Stop-Gracefully $fixtureProcess
    Stop-Gracefully $shellProcess
    $env:DCX_DATA_ROOT = $previousDataRoot
    $env:DCX_GLOBAL_KNOWLEDGE_FIXTURE = $previousFixture
    $env:DCX_GLOBAL_KNOWLEDGE_FIXTURE_ROOT = $previousFixtureRoot
    $env:DCX_OFFLINE_STARTUP = $previousOffline
}
