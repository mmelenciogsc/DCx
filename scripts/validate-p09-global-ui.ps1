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
$enrollmentDataRoot = Join-Path $runRoot "global-enrollment-ui-fixture-data"
$shellDataRoot = Join-Path $runRoot "global-ui-shell-data"
$trustRootPath = Join-Path $runRoot "global-registry-trust-root.spki"
$evidencePath = Join-Path $runRoot "global-knowledge-uia.json"
$fixtureProcess = $null
$enrollmentProcess = $null
$shellProcess = $null
$previousDataRoot = $env:DCX_DATA_ROOT
$previousFixture = $env:DCX_GLOBAL_KNOWLEDGE_FIXTURE
$previousFixtureRoot = $env:DCX_GLOBAL_KNOWLEDGE_FIXTURE_ROOT
$previousEnrollmentFixture = $env:DCX_GLOBAL_REGISTRY_ENROLLMENT_FIXTURE
$previousEnrollmentFixtureRoot = $env:DCX_GLOBAL_REGISTRY_ENROLLMENT_FIXTURE_ROOT
$previousUnconfiguredFixture = $env:DCX_GLOBAL_KNOWLEDGE_UNCONFIGURED_FIXTURE
$previousUnconfiguredFixtureRoot = $env:DCX_GLOBAL_KNOWLEDGE_UNCONFIGURED_FIXTURE_ROOT
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

function Toggle-ById($Root, [string]$AutomationId) {
    $element = Wait-For { Find-ById $Root $AutomationId } "The $AutomationId toggle was not exposed."
    $element.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Toggle()
    return $element
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
                -not [string]::IsNullOrWhiteSpace($candidate.Current.AccessKey)) { $candidate }
        } "The $automationId action lacks its required access key."
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

    $null = Wait-For {
        (Find-ById $fixtureWindow "SearchGlobalKnowledgeButton").Current.IsEnabled
    } "Search was not enabled after signed replica activation."
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

    $curve = [System.Security.Cryptography.ECCurve]::CreateFromFriendlyName("nistP256")
    $key = [System.Security.Cryptography.ECDsa]::Create($curve)
    try {
        if ($null -eq $key -or $key.KeySize -ne 256) {
            throw "The validation host did not create the required P-256 key."
        }
        $trustRootBytes = $key.ExportSubjectPublicKeyInfo()
        [IO.File]::WriteAllBytes($trustRootPath, $trustRootBytes)
        $trustRootSha256 = [Convert]::ToHexString(
            [System.Security.Cryptography.SHA256]::HashData($trustRootBytes)).ToLowerInvariant()
    }
    finally {
        $key.Dispose()
    }

    $env:DCX_DATA_ROOT = $enrollmentDataRoot
    $env:DCX_GLOBAL_REGISTRY_ENROLLMENT_FIXTURE = "1"
    $env:DCX_GLOBAL_REGISTRY_ENROLLMENT_FIXTURE_ROOT = $runRoot
    $enrollmentProcess = Start-Process $desktop `
        -ArgumentList "--global-registry-enrollment-fixture" -PassThru
    $enrollmentWindow = Wait-For {
        Find-WindowByProcessAndId $enrollmentProcess.Id "GlobalRegistryEnrollmentWindow"
    } "The global registry enrollment fixture window did not appear."
    $enrollmentHeading = Wait-For {
        $item = Find-ById $enrollmentWindow "GlobalRegistryEnrollmentHeading"
        if ($null -ne $item -and $item.Current.HasKeyboardFocus) { $item }
    } "Initial keyboard focus did not land on the enrollment heading."
    $enrollmentBoundary = Find-ById $enrollmentWindow "GlobalRegistryEnrollmentBoundary"
    if ($null -eq $enrollmentBoundary -or $enrollmentBoundary.Current.Name -notlike
        "*validation fixture*candidate is stored only in this process*No Windows credential*registry request*") {
        throw "The isolated enrollment fixture boundary was not exposed accessibly."
    }

    $enrollmentActionIds = @(
        "BrowseGlobalTrustRootButton",
        "ReviewGlobalRegistryEnrollmentButton",
        "ChangeGlobalRegistryEnrollmentCandidateButton",
        "ApproveGlobalRegistryEnrollmentBox",
        "SaveGlobalRegistryEnrollmentButton",
        "ConfirmClearGlobalRegistryEnrollmentBox",
        "ClearGlobalRegistryEnrollmentButton")
    $enrollmentAccessKeys = @()
    foreach ($automationId in $enrollmentActionIds) {
        $action = Wait-For {
            $candidate = Find-ById $enrollmentWindow $automationId
            if ($null -ne $candidate -and
                -not [string]::IsNullOrWhiteSpace($candidate.Current.AccessKey)) { $candidate }
        } "The $automationId enrollment action lacks its required access key."
        $enrollmentAccessKeys += $action.Current.AccessKey
    }
    if (@($enrollmentAccessKeys | Sort-Object -Unique).Count -ne $enrollmentAccessKeys.Count) {
        throw "Global registry enrollment actions expose duplicate access keys."
    }

    foreach ($disabledId in @(
        "ChangeGlobalRegistryEnrollmentCandidateButton",
        "ApproveGlobalRegistryEnrollmentBox",
        "SaveGlobalRegistryEnrollmentButton",
        "ClearGlobalRegistryEnrollmentButton")) {
        if ((Find-ById $enrollmentWindow $disabledId).Current.IsEnabled) {
            throw "$disabledId was enabled before its explicit prerequisite."
        }
    }
    $initialApproval = Find-ById $enrollmentWindow "ApproveGlobalRegistryEnrollmentBox"
    if ($initialApproval.GetCurrentPattern(
            [System.Windows.Automation.TogglePattern]::Pattern).Current.ToggleState -ne
        [System.Windows.Automation.ToggleState]::Off) {
        throw "Global registry enrollment approval was not initially unchecked."
    }

    Set-Value $enrollmentWindow "GlobalTrustRootPath" $trustRootPath
    Set-Value $enrollmentWindow "GlobalRegistryBaseUri" "http://127.0.0.1:5087"
    Invoke-ById $enrollmentWindow "ReviewGlobalRegistryEnrollmentButton" | Out-Null
    $exactReview = Wait-For {
        $value = Get-Value $enrollmentWindow "GlobalRegistryEnrollmentReview"
        if ($value -like "*Trust-root file: global-registry-trust-root.spki.*" -and
            $value -like "*Trust-root SHA-256: $trustRootSha256.*" -and
            $value -like "*Registry base address: http://127.0.0.1:5087/.*" -and
            $value -like "*Network requests during review: 0.*") { $value }
    } "The exact trust-root and endpoint review was not exposed."
    if ((Get-Value $enrollmentWindow "GlobalTrustRootPath") -ne
        "global-registry-trust-root.spki") {
        throw "The reviewed trust-root control retained an unnecessary full path."
    }
    if ((Find-ById $enrollmentWindow "ApproveGlobalRegistryEnrollmentBox").Current.IsEnabled -ne $true -or
        (Find-ById $enrollmentWindow "SaveGlobalRegistryEnrollmentButton").Current.IsEnabled) {
        throw "Review did not enable approval while retaining the save interlock."
    }

    Invoke-ById $enrollmentWindow "ChangeGlobalRegistryEnrollmentCandidateButton" | Out-Null
    $changedStatus = Wait-For {
        $item = Find-ById $enrollmentWindow "GlobalRegistryEnrollmentStatus"
        if ($null -ne $item -and $item.Current.Name -like
            "Candidate review and approval were cleared.*") { $item.Current.Name }
    } "Changing the candidate did not revoke the prior review."
    if ((Find-ById $enrollmentWindow "ApproveGlobalRegistryEnrollmentBox").Current.IsEnabled) {
        throw "Candidate change retained approval authority."
    }

    Set-Value $enrollmentWindow "GlobalTrustRootPath" $trustRootPath
    Set-Value $enrollmentWindow "GlobalRegistryBaseUri" "http://127.0.0.1:5087/"
    Invoke-ById $enrollmentWindow "ReviewGlobalRegistryEnrollmentButton" | Out-Null
    $null = Wait-For {
        (Find-ById $enrollmentWindow "ApproveGlobalRegistryEnrollmentBox").Current.IsEnabled
    } "The replacement candidate was not reviewable."
    Toggle-ById $enrollmentWindow "ApproveGlobalRegistryEnrollmentBox" | Out-Null
    $null = Wait-For {
        (Find-ById $enrollmentWindow "SaveGlobalRegistryEnrollmentButton").Current.IsEnabled
    } "Explicit approval did not enable save."
    Invoke-ById $enrollmentWindow "SaveGlobalRegistryEnrollmentButton" | Out-Null
    $savedStatus = Wait-For {
        $item = Find-ById $enrollmentWindow "GlobalRegistryEnrollmentStatus"
        if ($null -ne $item -and $item.Current.Name -like
            "Enrollment saved in the isolated validation store. Network requests: 0.*") {
            $item.Current.Name
        }
    } "The approved enrollment was not saved through the isolated workflow."

    Toggle-ById $enrollmentWindow "ConfirmClearGlobalRegistryEnrollmentBox" | Out-Null
    $null = Wait-For {
        (Find-ById $enrollmentWindow "ClearGlobalRegistryEnrollmentButton").Current.IsEnabled
    } "Explicit removal confirmation did not enable clear."
    Invoke-ById $enrollmentWindow "ClearGlobalRegistryEnrollmentButton" | Out-Null
    $clearedStatus = Wait-For {
        $item = Find-ById $enrollmentWindow "GlobalRegistryEnrollmentStatus"
        if ($null -ne $item -and $item.Current.Name -like
            "Global registry enrollment was cleared. Network requests: 0.*") { $item.Current.Name }
    } "The enrollment authority was not cleared through the accessible workflow."
    $enrollmentConnections = @(Get-NetTCPConnection `
        -OwningProcess $enrollmentProcess.Id -ErrorAction SilentlyContinue |
        Where-Object { $_.State -in @("Established", "Listen", "SynSent", "SynReceived") })
    if ($enrollmentConnections.Count -ne 0) {
        throw "The enrollment UI fixture unexpectedly opened a network socket."
    }

    Stop-Gracefully $enrollmentProcess
    $enrollmentProcess = $null

    $env:DCX_DATA_ROOT = $shellDataRoot
    $env:DCX_GLOBAL_KNOWLEDGE_FIXTURE = $null
    $env:DCX_GLOBAL_KNOWLEDGE_FIXTURE_ROOT = $null
    $env:DCX_GLOBAL_REGISTRY_ENROLLMENT_FIXTURE = $null
    $env:DCX_GLOBAL_REGISTRY_ENROLLMENT_FIXTURE_ROOT = $null
    $env:DCX_GLOBAL_KNOWLEDGE_UNCONFIGURED_FIXTURE = "1"
    $env:DCX_GLOBAL_KNOWLEDGE_UNCONFIGURED_FIXTURE_ROOT = $runRoot
    $shellProcess = Start-Process $desktop `
        -ArgumentList "--global-knowledge-unconfigured-fixture" -PassThru
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
    $enrollmentEntry = Find-ById $unavailableWindow "GlobalRegistryEnrollmentButton"
    if ($null -eq $enrollmentEntry -or -not $enrollmentEntry.Current.IsEnabled -or
        -not $enrollmentEntry.Current.IsKeyboardFocusable -or
        [string]::IsNullOrWhiteSpace($enrollmentEntry.Current.AccessKey)) {
        throw "The protected enrollment entry was not keyboard accessible from Global knowledge."
    }
    $enrollmentEntry.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    $productEnrollmentWindow = Wait-For {
        Find-WindowByProcessAndId $shellProcess.Id "GlobalRegistryEnrollmentWindow"
    } "The protected enrollment window did not open from Global knowledge."
    $productBoundary = Find-ById $productEnrollmentWindow "GlobalRegistryEnrollmentBoundary"
    if ($null -eq $productBoundary -or $productBoundary.Current.Name -notlike
        "*stored in Windows Credential Manager*HTTPS is mandatory*") {
        throw "The production enrollment trust boundary was not exposed accessibly."
    }
    $productEnrollmentWindow.GetCurrentPattern(
        [System.Windows.Automation.WindowPattern]::Pattern).Close()
    $null = Wait-For {
        $item = Find-ById $unavailableWindow "GlobalRegistryEnrollmentButton"
        if ($null -ne $item -and $item.Current.HasKeyboardFocus) { $item }
    } "Keyboard focus did not return to the enrollment entry after closing its window."

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
        enrollmentActionAccessKeys = $enrollmentAccessKeys
        enrollmentExactReviewSha256 = $trustRootSha256
        enrollmentReviewInvalidation = "pass"
        enrollmentApprovalInterlock = "pass"
        enrollmentClearInterlock = "pass"
        enrollmentNetworkSockets = 0
        shellEntry = "pass"
        protectedEnrollmentEntry = "pass"
        protectedEnrollmentFocusReturn = "pass"
        unconfiguredFailClosed = "pass"
        productionActions = 0
    } | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $evidencePath -Encoding utf8

    "P09 global knowledge UIA=PASS"
    "P09 explicit signed synchronization UIA=PASS"
    "P09 offline global CKB/CCB search UIA=PASS"
    "P09 private-collection and network isolation UIA=PASS"
    "P09 exact global registry enrollment UIA=PASS"
    "P09 enrollment approval/review/revocation interlocks UIA=PASS"
    "P09 unconfigured default fail-closed UIA=PASS"
    "P09 global UIA evidence=$evidencePath"
}
finally {
    Stop-Gracefully $fixtureProcess
    Stop-Gracefully $enrollmentProcess
    Stop-Gracefully $shellProcess
    $env:DCX_DATA_ROOT = $previousDataRoot
    $env:DCX_GLOBAL_KNOWLEDGE_FIXTURE = $previousFixture
    $env:DCX_GLOBAL_KNOWLEDGE_FIXTURE_ROOT = $previousFixtureRoot
    $env:DCX_GLOBAL_REGISTRY_ENROLLMENT_FIXTURE = $previousEnrollmentFixture
    $env:DCX_GLOBAL_REGISTRY_ENROLLMENT_FIXTURE_ROOT = $previousEnrollmentFixtureRoot
    $env:DCX_GLOBAL_KNOWLEDGE_UNCONFIGURED_FIXTURE = $previousUnconfiguredFixture
    $env:DCX_GLOBAL_KNOWLEDGE_UNCONFIGURED_FIXTURE_ROOT = $previousUnconfiguredFixtureRoot
    $env:DCX_OFFLINE_STARTUP = $previousOffline
}
