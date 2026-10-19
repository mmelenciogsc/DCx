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
    throw "The P11 recipient UIA run root must be beneath the repository test-results directory."
}

$desktop = Join-Path $repoRoot "src\DCx.Desktop\bin\Release\net10.0-windows10.0.19041.0\DCx.exe"
$coreAssembly = Join-Path $repoRoot "src\DCx.Core\bin\Release\net10.0\DCx.Core.dll"
$dataRoot = Join-Path $runRoot "recipient-uia-data"
$evidencePath = Join-Path $runRoot "recipient-snapshot-uia.json"
$fixtureSuffix = [Guid]::NewGuid().ToString("N")
$contributor = "contributor:uia-$fixtureSuffix"
$destination = "uia-$fixtureSuffix@personal.example.com"
$destinationBytes = [Text.Encoding]::UTF8.GetBytes($destination)
try {
    $destinationDigest = [Convert]::ToHexString(
        [Security.Cryptography.SHA256]::HashData($destinationBytes)).ToLowerInvariant()
}
finally {
    [Array]::Clear($destinationBytes, 0, $destinationBytes.Length)
}

$previousDataRoot = $env:DCX_DATA_ROOT
$previousOfflineStartup = $env:DCX_OFFLINE_STARTUP
$previousCareOperationsFixture = $env:DCX_CARE_OPERATIONS_FIXTURE
$previousCareContributorDashboardFixture = $env:DCX_CARE_CONTRIBUTOR_DASHBOARD_FIXTURE
$process = $null
$store = $null

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
[void][Reflection.Assembly]::LoadFrom($coreAssembly)

function Wait-For([scriptblock]$Condition, [string]$FailureMessage, [int]$Attempts = 80) {
    for ($attempt = 0; $attempt -lt $Attempts; $attempt++) {
        $result = & $Condition
        if ($result) { return $result }
        Start-Sleep -Milliseconds 250
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

function Set-Value($Root, [string]$AutomationId, [string]$Value) {
    $element = Wait-For { Find-ById $Root $AutomationId } "The $AutomationId field was not exposed."
    $element.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($Value)
}

function Get-Value($Root, [string]$AutomationId) {
    $element = Wait-For { Find-ById $Root $AutomationId } "The $AutomationId value was not exposed."
    return $element.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value
}

function Invoke-ById($Root, [string]$AutomationId) {
    $element = Wait-For { Find-ById $Root $AutomationId } "The $AutomationId action was not exposed."
    $element.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    return $element
}

if (-not (Test-Path -LiteralPath $desktop -PathType Leaf)) {
    throw "Build DCx Desktop before running P11 recipient UIA validation."
}
New-Item -ItemType Directory -Path $runRoot -Force | Out-Null
$env:DCX_DATA_ROOT = $dataRoot
$env:DCX_OFFLINE_STARTUP = "1"
$env:DCX_CARE_OPERATIONS_FIXTURE = "1"
$env:DCX_CARE_CONTRIBUTOR_DASHBOARD_FIXTURE = "1"

try {
    $process = Start-Process $desktop -PassThru
    $mainWindow = Wait-For {
        $condition = [System.Windows.Automation.PropertyCondition]::new(
            [System.Windows.Automation.AutomationElement]::ProcessIdProperty,
            $process.Id)
        [System.Windows.Automation.AutomationElement]::RootElement.FindFirst(
            [System.Windows.Automation.TreeScope]::Children,
            $condition)
    } "The DCx main window did not appear."

    Invoke-ById $mainWindow "CareRecipientsButton" | Out-Null
    $recipientWindow = Wait-For {
        Find-WindowByProcessAndId $process.Id "CareRecipientsWindow"
    } "The accessible CARE payout recipient window did not open."
    $heading = Wait-For {
        Find-ById $recipientWindow "CareRecipientsHeading"
    } "The CARE recipient heading was not exposed."
    $approval = Wait-For {
        Find-ById $recipientWindow "ApproveCareRecipientBox"
    } "The recipient approval checkbox was not exposed."
    $approvalToggle = $approval.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
    if ($approvalToggle.Current.ToggleState -ne [System.Windows.Automation.ToggleState]::Off) {
        throw "Recipient owner approval did not default to unchecked."
    }
    $revokeApproval = Find-ById $recipientWindow "ApproveCareRecipientRevocationBox"
    if ($null -eq $revokeApproval -or
        $revokeApproval.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern).Current.ToggleState -ne
            [System.Windows.Automation.ToggleState]::Off) {
        throw "Recipient revocation approval did not default to unchecked."
    }

    $actionIds = @(
        "ReviewCareRecipientButton",
        "ApproveCareRecipientBox",
        "SaveCareRecipientButton",
        "RefreshCareRecipientsButton",
        "ApproveCareRecipientRevocationBox",
        "RevokeCareRecipientButton")
    $accessKeys = @()
    foreach ($automationId in $actionIds) {
        $action = Find-ById $recipientWindow $automationId
        if ($null -eq $action -or -not $action.Current.IsKeyboardFocusable -or
            [string]::IsNullOrWhiteSpace($action.Current.AccessKey)) {
            throw "The $automationId action lacks a keyboard-focusable access key."
        }
        $accessKeys += $action.Current.AccessKey
    }
    if (@($accessKeys | Sort-Object -Unique).Count -ne $accessKeys.Count) {
        throw "CARE recipient actions expose duplicate access keys."
    }

    Set-Value $recipientWindow "CareRecipientContributor" $contributor
    Set-Value $recipientWindow "CareRecipientDestination" $destination
    Invoke-ById $recipientWindow "SaveCareRecipientButton" | Out-Null
    $null = Wait-For {
        $status = Find-ById $recipientWindow "CareRecipientsStatus"
        if ($null -ne $status -and $status.Current.Name.StartsWith(
            "Recipient snapshot was not saved.", [StringComparison]::Ordinal)) { $status }
    } "Saving without review and approval did not fail closed."

    Invoke-ById $recipientWindow "ReviewCareRecipientButton" | Out-Null
    $review = Wait-For {
        $value = Get-Value $recipientWindow "CareRecipientReview"
        if ($value -like "*Exact recipient email: $destination*" -and
            $value -like "*Destination SHA-256: $destinationDigest*" -and
            $value -like "*PayPal verification and eligibility: not established.*") { $value }
    } "The exact recipient review was not exposed through UI Automation."
    if ($approvalToggle.Current.ToggleState -ne [System.Windows.Automation.ToggleState]::Off) {
        throw "Recipient owner approval changed without user action."
    }

    Set-Value $recipientWindow "CareRecipientDestination" "changed-$destination"
    if ($approvalToggle.Current.ToggleState -ne [System.Windows.Automation.ToggleState]::Off -or
        (Get-Value $recipientWindow "CareRecipientReview") -notlike "*input changed*") {
        throw "Changing the recipient candidate did not invalidate review and approval."
    }
    Set-Value $recipientWindow "CareRecipientDestination" $destination
    Invoke-ById $recipientWindow "ReviewCareRecipientButton" | Out-Null
    $null = Wait-For {
        $value = Get-Value $recipientWindow "CareRecipientReview"
        if ($value -like "*Exact recipient email: $destination*" -and
            $value -like "*Destination SHA-256: $destinationDigest*") { $value }
    } "The restored exact recipient candidate was not reviewed before approval."
    $approvalToggle.Toggle()
    $null = Wait-For {
        if ($approvalToggle.Current.ToggleState -eq [System.Windows.Automation.ToggleState]::On) { $true }
    } "Recipient owner approval did not enter the checked state."
    Invoke-ById $recipientWindow "SaveCareRecipientButton" | Out-Null
    $savedStatus = Wait-For {
        $status = Find-ById $recipientWindow "CareRecipientsStatus"
        if ($null -ne $status -and $status.Current.Name -like
            "Snapshot recipient:* saved in protected local storage.*not PayPal-verified*payout-eligible*No network request occurred.*") { $status }
    } "The approved protected recipient snapshot was not saved accessibly."
    $savedStatusName = [string]$savedStatus.Current.Name
    if (-not [string]::IsNullOrEmpty((Get-Value $recipientWindow "CareRecipientDestination")) -or
        -not [string]::IsNullOrEmpty((Get-Value $recipientWindow "CareRecipientContributor"))) {
        throw "The exact recipient input was not cleared after approval."
    }
    if ($approvalToggle.Current.ToggleState -ne [System.Windows.Automation.ToggleState]::Off) {
        throw "Recipient owner approval was not reset after saving."
    }

    $list = Find-ById $recipientWindow "CareRecipientSnapshotsList"
    $listItemCondition = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
        [System.Windows.Automation.ControlType]::ListItem)
    $activeItem = Wait-For {
        $items = $list.FindAll([System.Windows.Automation.TreeScope]::Descendants, $listItemCondition)
        @($items | Where-Object {
            $_.Current.Name -like "Active owner-approved snapshot*$contributor*$destinationDigest*PayPal verification and eligibility not established.*"
        }) | Select-Object -First 1
    } "The minimized active snapshot metadata was not exposed."
    if ($activeItem.Current.Name.Contains($destination, [StringComparison]::OrdinalIgnoreCase)) {
        throw "The snapshot metadata list exposed the plaintext recipient email."
    }

    $statePath = Join-Path $dataRoot "finance\payout-recipients\snapshots.json"
    if ((Get-Content -LiteralPath $statePath -Raw).Contains($destination, [StringComparison]::OrdinalIgnoreCase)) {
        throw "The local metadata file retained the plaintext recipient email."
    }

    $store = [DCx.Core.Finance.PayoutRecipientSnapshotStore]::OpenAsync(
        $dataRoot, [Threading.CancellationToken]::None).GetAwaiter().GetResult()
    $summaries = $store.ListAsync([Threading.CancellationToken]::None).GetAwaiter().GetResult()
    if ($summaries.Count -ne 1 -or -not $summaries[0].IsActive -or
        $summaries[0].DestinationSha256 -ne $destinationDigest) {
        throw "The native UI did not persist one exact active recipient snapshot."
    }
    $snapshotId = $summaries[0].SnapshotId
    $resolved = $store.ResolveAsync(
        $snapshotId, [Threading.CancellationToken]::None).AsTask().GetAwaiter().GetResult()
    if ($null -eq $resolved -or $resolved.Email -ne $destination -or
        $resolved.ContributorPseudonym -ne $contributor -or $resolved.IsVerifiedAndEligible) {
        throw "The protected recipient binding or mandatory ineligible state did not survive reopen."
    }

    $activeItem.GetCurrentPattern(
        [System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    $revokeToggle = $revokeApproval.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
    $revokeToggle.Toggle()
    Invoke-ById $recipientWindow "RevokeCareRecipientButton" | Out-Null
    $revokedStatus = Wait-For {
        $status = Find-ById $recipientWindow "CareRecipientsStatus"
        if ($null -ne $status -and $status.Current.Name -like
            "Snapshot recipient:* revoked. Its protected email was deleted. No PayPal request occurred.*") { $status }
    } "The recipient snapshot was not revoked accessibly."
    $revokedStatusName = [string]$revokedStatus.Current.Name
    if ($revokeToggle.Current.ToggleState -ne [System.Windows.Automation.ToggleState]::Off) {
        throw "Recipient revocation approval was not reset."
    }
    $resolvedAfterRevocation = $store.ResolveAsync(
        $snapshotId, [Threading.CancellationToken]::None).AsTask().GetAwaiter().GetResult()
    if ($null -ne $resolvedAfterRevocation) {
        throw "The revoked recipient snapshot still resolved its protected email."
    }

    Invoke-ById $mainWindow "CareOperationsButton" | Out-Null
    $operationsWindow = Wait-For {
        Find-WindowByProcessAndId $process.Id "CareOperationsWindow"
    } "The accessible CARE verified-event operations window did not open."
    $operationsHeading = Wait-For {
        Find-ById $operationsWindow "CareOperationsHeading"
    } "The CARE operations heading was not exposed."
    $null = Wait-For {
        if ($operationsHeading.Current.HasKeyboardFocus) { $true }
    } "Keyboard focus did not enter the CARE operations heading."
    $operationsBoundary = Wait-For {
        $element = Find-ById $operationsWindow "CareOperationsBoundary"
        if ($null -ne $element -and $element.Current.Name -like
            "Deterministic local validation fixture*Provider credentials*production actions are unavailable.*") { $element }
    } "The inert fixture boundary was not exposed."
    $operationsSummaryBefore = Wait-For {
        $value = Get-Value $operationsWindow "CareOperationsSummary"
        if ($value -like "*Status: operator_action_required.*" -and
            $value -like "*Pending: 2.*" -and
            $value -like "*Due now: 2.*" -and
            $value -like "*Blocked: 1.*" -and
            $value -like "*Local processing available: yes.*" -and
            $value -like "*Data source: deterministic local fixture.*") { $value }
    } "The generated aggregate CARE operations state was not exposed."
    $operationsApproval = Wait-For {
        Find-ById $operationsWindow "ApproveCareOperationsProcessBox"
    } "The CARE operations approval checkbox was not exposed."
    $operationsApprovalToggle = $operationsApproval.GetCurrentPattern(
        [System.Windows.Automation.TogglePattern]::Pattern)
    if ($operationsApprovalToggle.Current.ToggleState -ne
        [System.Windows.Automation.ToggleState]::Off) {
        throw "CARE operations approval did not default to unchecked."
    }

    $operationsActionIds = @(
        "RefreshCareOperationsButton",
        "ApproveCareOperationsProcessBox",
        "ProcessDueCareOperationsButton",
        "LoadBlockedCareCasesButton",
        "LoadFailedPayoutReviewsButton")
    $operationsAccessKeys = @()
    foreach ($automationId in $operationsActionIds) {
        $action = Find-ById $operationsWindow $automationId
        if ($null -eq $action -or -not $action.Current.IsKeyboardFocusable -or
            [string]::IsNullOrWhiteSpace($action.Current.AccessKey)) {
            throw "The $automationId action lacks a keyboard-focusable access key."
        }
        $operationsAccessKeys += $action.Current.AccessKey
    }
    if (@($operationsAccessKeys | Sort-Object -Unique).Count -ne
        $operationsAccessKeys.Count) {
        throw "CARE operations actions expose duplicate access keys."
    }

    Invoke-ById $operationsWindow "LoadBlockedCareCasesButton" | Out-Null
    $blockedList = Wait-For {
        $element = Find-ById $operationsWindow "BlockedCareCasesList"
        if ($null -ne $element) {
            $items = $element.FindAll(
                [System.Windows.Automation.TreeScope]::Children,
                [System.Windows.Automation.Condition]::TrueCondition)
            if ($items.Count -eq 1) { $element }
        }
    } "The privacy-minimized blocked CARE case was not exposed."
    $blockedItems = $blockedList.FindAll(
        [System.Windows.Automation.TreeScope]::Children,
        [System.Windows.Automation.Condition]::TrueCondition)
    $blockedItems[0].GetCurrentPattern(
        [System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    $blockedDetailsBefore = Wait-For {
        $value = Get-Value $operationsWindow "BlockedCareCaseDetails"
        if ($value -like "*Opaque case reference: 0123456789abcdef0123456789abcdef.*" -and
            $value -like "*Event kind: refund.*" -and
            $value -like "*Reason: policy_rejected.*" -and
            $value -like "*Current disposition: unreviewed.*" -and
            $value -notlike "*subscription*" -and
            $value -notlike "*recipient*") { $value }
    } "The exact privacy-minimized blocked CARE case review was not exposed."
    $blockedActionIds = @(
        "InvestigateBlockedCareCaseOption",
        "RetainBlockedCareCaseOption",
        "ApproveBlockedCareDispositionBox",
        "RecordBlockedCareDispositionButton")
    foreach ($automationId in $blockedActionIds) {
        $action = Find-ById $operationsWindow $automationId
        if ($null -eq $action -or -not $action.Current.IsKeyboardFocusable -or
            [string]::IsNullOrWhiteSpace($action.Current.AccessKey)) {
            throw "The $automationId action lacks a keyboard-focusable access key."
        }
        $operationsAccessKeys += $action.Current.AccessKey
    }
    if (@($operationsAccessKeys | Sort-Object -Unique).Count -ne
        $operationsAccessKeys.Count) {
        throw "CARE operations and blocked-case actions expose duplicate access keys."
    }
    $dispositionApproval = Find-ById $operationsWindow "ApproveBlockedCareDispositionBox"
    $dispositionApprovalToggle = $dispositionApproval.GetCurrentPattern(
        [System.Windows.Automation.TogglePattern]::Pattern)
    if ($dispositionApprovalToggle.Current.ToggleState -ne
        [System.Windows.Automation.ToggleState]::Off) {
        throw "Blocked-case disposition approval did not default to unchecked."
    }
    Invoke-ById $operationsWindow "RecordBlockedCareDispositionButton" | Out-Null
    $null = Wait-For {
        $status = Find-ById $operationsWindow "CareOperationsStatus"
        if ($null -ne $status -and $status.Current.Name -like
            "No disposition was recorded.*explicitly approve it first.*") { $status }
    } "Blocked-case disposition without approval did not fail closed."
    if ($dispositionApprovalToggle.Current.ToggleState -ne
        [System.Windows.Automation.ToggleState]::Off -or
        -not $dispositionApproval.Current.HasKeyboardFocus) {
        throw "Denied blocked-case disposition did not reset and focus explicit approval."
    }
    $investigationOption = Find-ById $operationsWindow "InvestigateBlockedCareCaseOption"
    $investigationOption.GetCurrentPattern(
        [System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    $dispositionApprovalToggle.Toggle()
    Invoke-ById $operationsWindow "RecordBlockedCareDispositionButton" | Out-Null
    $dispositionStatus = Wait-For {
        $status = Find-ById $operationsWindow "CareOperationsStatus"
        if ($null -ne $status -and $status.Current.Name -eq
            "Non-financial disposition recorded as investigation_requested. Provider calls 0. Financial mutations 0. The event remains blocked.") { $status }
    } "The approved non-financial blocked-case disposition was not announced."
    $dispositionStatusName = [string]$dispositionStatus.Current.Name
    $blockedDetailsAfter = Wait-For {
        $element = Find-ById $operationsWindow "BlockedCareCaseDetails"
        $value = $element.GetCurrentPattern(
            [System.Windows.Automation.ValuePattern]::Pattern).Current.Value
        if ($value -like "*Current disposition: investigation_requested.*" -and
            $value -like "*no authority to change the blocked event or any financial state.*" -and
            $element.Current.HasKeyboardFocus) { $value }
    } "The recorded disposition or focus was not exposed on the exact case."
    if ($dispositionApprovalToggle.Current.ToggleState -ne
        [System.Windows.Automation.ToggleState]::Off) {
        throw "Blocked-case disposition approval was not reset after recording."
    }

    Invoke-ById $operationsWindow "LoadFailedPayoutReviewsButton" | Out-Null
    $failedPayoutList = Wait-For {
        $element = Find-ById $operationsWindow "FailedPayoutReviewsList"
        if ($null -ne $element) {
            $items = $element.FindAll(
                [System.Windows.Automation.TreeScope]::Children,
                [System.Windows.Automation.Condition]::TrueCondition)
            if ($items.Count -eq 1) { $element }
        }
    } "The privacy-minimized failed payout hold was not exposed."
    $failedPayoutItems = $failedPayoutList.FindAll(
        [System.Windows.Automation.TreeScope]::Children,
        [System.Windows.Automation.Condition]::TrueCondition)
    $failedPayoutItems[0].GetCurrentPattern(
        [System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    $failedPayoutDetailsBefore = Wait-For {
        $value = Get-Value $operationsWindow "FailedPayoutReviewDetails"
        if ($value -like "*Opaque review reference: fedcba9876543210fedcba9876543210.*" -and
            $value -like "*CARE period: care:2026-05.*" -and
            $value -like "*Amount: USD 7.00.*" -and
            $value -like "*Closed failure code: recipient_unverified.*" -and
            $value -like "*Hold code: failed_item_hold.*" -and
            $value -like "*Local notice code: not_ready.*" -and
            $value -like "*Current disposition: unreviewed.*" -and
            $value -notlike "*@*" -and
            $value -notlike "*PayPal*" -and
            $value -notlike "*destination*" -and
            $value -notlike "*account*") { $value }
    } "The exact privacy-minimized failed payout hold review was not exposed."
    $failedPayoutActionIds = @(
        "RetainFailedPayoutHoldOption",
        "RequestFailedPayoutRecipientUpdateOption",
        "CancelFailedPayoutRetryOption",
        "ApproveFailedPayoutDispositionBox",
        "RecordFailedPayoutDispositionButton")
    foreach ($automationId in $failedPayoutActionIds) {
        $action = Find-ById $operationsWindow $automationId
        if ($null -eq $action -or -not $action.Current.IsKeyboardFocusable -or
            [string]::IsNullOrWhiteSpace($action.Current.AccessKey)) {
            throw "The $automationId action lacks a keyboard-focusable access key."
        }
        $operationsAccessKeys += $action.Current.AccessKey
    }
    if (@($operationsAccessKeys | Sort-Object -Unique).Count -ne
        $operationsAccessKeys.Count) {
        throw "CARE operations remediation actions expose duplicate access keys."
    }
    $failedPayoutApproval = Find-ById $operationsWindow "ApproveFailedPayoutDispositionBox"
    $failedPayoutApprovalToggle = $failedPayoutApproval.GetCurrentPattern(
        [System.Windows.Automation.TogglePattern]::Pattern)
    if ($failedPayoutApprovalToggle.Current.ToggleState -ne
        [System.Windows.Automation.ToggleState]::Off) {
        throw "Failed-payout disposition approval did not default to unchecked."
    }
    Invoke-ById $operationsWindow "RecordFailedPayoutDispositionButton" | Out-Null
    $null = Wait-For {
        $status = Find-ById $operationsWindow "CareOperationsStatus"
        if ($null -ne $status -and $status.Current.Name -like
            "No failed-payout decision was recorded.*explicitly approve it first.*") { $status }
    } "Failed-payout disposition without approval did not fail closed."
    if ($failedPayoutApprovalToggle.Current.ToggleState -ne
        [System.Windows.Automation.ToggleState]::Off -or
        -not $failedPayoutApproval.Current.HasKeyboardFocus) {
        throw "Denied failed-payout disposition did not reset and focus explicit approval."
    }
    $requestUpdateOption = Find-ById $operationsWindow "RequestFailedPayoutRecipientUpdateOption"
    $requestUpdateOption.GetCurrentPattern(
        [System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
    $failedPayoutApprovalToggle.Toggle()
    Invoke-ById $operationsWindow "RecordFailedPayoutDispositionButton" | Out-Null
    $failedPayoutStatus = Wait-For {
        $status = Find-ById $operationsWindow "CareOperationsStatus"
        if ($null -ne $status -and $status.Current.Name -eq
            "Failed-payout decision recorded as request_recipient_update. Local dashboard notice local_dashboard_notice_ready. Provider calls 0. Financial mutations 0. Payout attempts 0. Notification transports 0. No retry was sent and the payout remains held.") { $status }
    } "The approved zero-authority failed-payout disposition was not announced."
    $failedPayoutStatusName = [string]$failedPayoutStatus.Current.Name
    $failedPayoutDetailsAfter = Wait-For {
        $element = Find-ById $operationsWindow "FailedPayoutReviewDetails"
        $value = $element.GetCurrentPattern(
            [System.Windows.Automation.ValuePattern]::Pattern).Current.Value
        if ($value -like "*Local notice code: local_dashboard_notice_ready.*" -and
            $value -like "*Current disposition: request_recipient_update.*" -and
            $value -like "*cannot retry or release a payout*send a notification*contact a provider.*" -and
            $element.Current.HasKeyboardFocus) { $value }
    } "The recorded failed-payout disposition or focus was not exposed."
    if ($failedPayoutApprovalToggle.Current.ToggleState -ne
        [System.Windows.Automation.ToggleState]::Off) {
        throw "Failed-payout disposition approval was not reset after recording."
    }
    $failedPayoutRecordButton = Find-ById $operationsWindow "RecordFailedPayoutDispositionButton"
    if ($null -eq $failedPayoutRecordButton -or $failedPayoutRecordButton.Current.IsEnabled) {
        throw "Failed-payout disposition replay remained enabled after the decision was recorded."
    }

    Invoke-ById $operationsWindow "ProcessDueCareOperationsButton" | Out-Null
    $null = Wait-For {
        $status = Find-ById $operationsWindow "CareOperationsStatus"
        if ($null -ne $status -and $status.Current.Name -like
            "No records were processed.*explicitly approve one local cycle first.*") { $status }
    } "CARE operations processing without approval did not fail closed."
    if ($operationsApprovalToggle.Current.ToggleState -ne
        [System.Windows.Automation.ToggleState]::Off -or
        -not $operationsApproval.Current.HasKeyboardFocus) {
        throw "Denied CARE processing did not reset and focus explicit approval."
    }

    $operationsApprovalToggle.Toggle()
    $null = Wait-For {
        if ($operationsApprovalToggle.Current.ToggleState -eq
            [System.Windows.Automation.ToggleState]::On) { $true }
    } "CARE operations approval did not enter the checked state."
    Invoke-ById $operationsWindow "ProcessDueCareOperationsButton" | Out-Null
    $operationsCompletedStatus = Wait-For {
        $status = Find-ById $operationsWindow "CareOperationsStatus"
        if ($null -ne $status -and $status.Current.Name -eq
            "Bounded local cycle completed. Scanned 2. Completed 2. Still pending 0. Blocked 0. Provider calls 0.") { $status }
    } "The approved bounded CARE operations fixture did not complete accessibly."
    $operationsCompletedStatusName = [string]$operationsCompletedStatus.Current.Name
    $operationsSummaryAfter = Wait-For {
        $element = Find-ById $operationsWindow "CareOperationsSummary"
        $value = $element.GetCurrentPattern(
            [System.Windows.Automation.ValuePattern]::Pattern).Current.Value
        if ($value -like "*Status: operator_action_required.*" -and
            $value -like "*Pending: 0.*" -and
            $value -like "*Blocked: 1.*" -and
            $value -like "*Completed audit records: 2.*" -and
            $value -like "*Local processing available: no.*" -and
            $element.Current.HasKeyboardFocus) { $value }
    } "Completed CARE operations state or focus was not exposed."
    if ($operationsApprovalToggle.Current.ToggleState -ne
        [System.Windows.Automation.ToggleState]::Off) {
        throw "CARE operations approval was not reset after processing."
    }
    $processDueButton = Find-ById $operationsWindow "ProcessDueCareOperationsButton"
    if ($null -eq $processDueButton -or $processDueButton.Current.IsEnabled) {
        throw "CARE operations replay remained enabled without due records."
    }

    Invoke-ById $mainWindow "CareContributorDashboardButton" | Out-Null
    $dashboardWindow = Wait-For {
        Find-WindowByProcessAndId $process.Id "CareContributorDashboardWindow"
    } "The accessible CARE contributor dashboard did not open."
    $dashboardHeading = Wait-For {
        Find-ById $dashboardWindow "CareContributorDashboardHeading"
    } "The CARE contributor dashboard heading was not exposed."
    $null = Wait-For {
        if ($dashboardHeading.Current.HasKeyboardFocus) { $true }
    } "Keyboard focus did not enter the CARE contributor dashboard heading."
    $dashboardBoundary = Wait-For {
        $element = Find-ById $dashboardWindow "CareContributorDashboardBoundary"
        if ($null -ne $element -and $element.Current.Name -like
            "Deterministic local validation fixture*recipient destinations*network requests*production actions are unavailable.*") { $element }
    } "The generated-only CARE contributor dashboard boundary was not exposed."
    $dashboardSummary = Wait-For {
        $value = Get-Value $dashboardWindow "CareContributorDashboardSummary"
        if ($value -like "*Contributor pseudonym: contributor:dashboard-fixture.*" -and
            $value -like "*Currency: USD.*" -and
            $value -like "*Periods: 5.*" -and
            $value -like "*Local failed-payout notices: 1.*" -and
            $value -like "*Allocated: USD 25.00.*" -and
            $value -like "*Paid: USD 4.00.*" -and
            $value -like "*Outstanding or held: USD 21.00.*" -and
            $value -like "*Data source: deterministic local fixture.*") { $value }
    } "The exact generated CARE contributor summary was not exposed."
    $refreshDashboard = Find-ById $dashboardWindow "RefreshCareContributorDashboardButton"
    if ($null -eq $refreshDashboard -or -not $refreshDashboard.Current.IsKeyboardFocusable -or
        [string]::IsNullOrWhiteSpace($refreshDashboard.Current.AccessKey)) {
        throw "The CARE contributor dashboard refresh action lacks a keyboard-focusable access key."
    }
    $dashboardPeriods = Find-ById $dashboardWindow "CareContributorPeriodsList"
    $periodItems = Wait-For {
        $items = $dashboardPeriods.FindAll(
            [System.Windows.Automation.TreeScope]::Children,
            [System.Windows.Automation.Condition]::TrueCondition)
        if ($items.Count -eq 5) { $items }
    } "The five generated CARE contributor periods were not exposed."
    $periodNames = @()
    for ($periodIndex = 0; $periodIndex -lt $periodItems.Count; $periodIndex++) {
        $periodNames += [string]$periodItems[$periodIndex].Current.Name
    }
    $periodText = $periodNames -join "`n"
    foreach ($statusCode in @("withheld", "held_below_threshold", "pending", "partially_paid", "paid")) {
        if ($periodText -notlike "*Status $statusCode.*") {
            throw "The CARE contributor dashboard omitted status $statusCode. Observed: $periodText"
        }
    }
    foreach ($notice in @(
        "Notice: This allocation is withheld. No payout is scheduled.",
        "Notice: This allocation remains held below the payout threshold.",
        "Notice: A payout is pending. No contributor action is requested.",
        "Notice: Part was paid. The remaining amount stays held for review; no automatic retry occurs.",
        "Notice: This allocation was paid.")) {
        if ($periodText -notlike "*$notice*") {
            throw "The CARE contributor dashboard omitted local notice: $notice"
        }
    }
    if ($periodText -like "*@*" -or $periodText -like "*PayPal*" -or
        $periodText -like "*account*" -or $periodText -like "*recipient*") {
        throw "The CARE contributor period history exposed recipient or provider-account data."
    }
    $dashboardNotices = Find-ById $dashboardWindow "CareContributorNoticesList"
    $noticeItems = Wait-For {
        $items = $dashboardNotices.FindAll(
            [System.Windows.Automation.TreeScope]::Children,
            [System.Windows.Automation.Condition]::TrueCondition)
        if ($items.Count -eq 1) { $items }
    } "The generated local failed-payout notice was not exposed."
    $noticeText = [string]$noticeItems[0].Current.Name
    if ($noticeText -notlike "*care:2026-05.*Amount USD 7.00.*Payout recipient information needs review.*failed amount remains held*no automatic retry occurs.*local dashboard notice*No external message was sent.*" -or
        $noticeText -like "*snapshot:*" -or $noticeText -like "*batch:*" -or
        $noticeText -like "*personal.example*" -or $noticeText -like "*PayPal*") {
        throw "The generated failed-payout notice was incomplete or disclosed a forbidden binding."
    }
    Invoke-ById $dashboardWindow "RefreshCareContributorDashboardButton" | Out-Null
    $dashboardStatus = Wait-For {
        $status = Find-ById $dashboardWindow "CareContributorDashboardStatus"
        if ($null -ne $status -and $status.Current.Name -eq
            "Loaded 5 generated CARE periods and 1 local failed-payout notice record. This dashboard is read-only. Network requests 0. Notification transports 0. Payout attempts 0.") { $status }
    } "The read-only CARE contributor dashboard refresh was not announced."
    $null = Wait-For {
        $summary = Find-ById $dashboardWindow "CareContributorDashboardSummary"
        if ($summary.Current.HasKeyboardFocus) { $true }
    } "CARE contributor dashboard refresh did not move focus to the updated summary."

    [ordered]@{
        schema_version = 4
        heading = $heading.Current.Name
        owner_approval_default_unchecked = $true
        revocation_approval_default_unchecked = $true
        exact_review_digest = $destinationDigest
        exact_destination_cleared_after_save = $true
        metadata_plaintext_destination_matches = 0
        provider_verified_and_eligible = $false
        network_requests = 0
        payout_attempts = 0
        snapshot_revoked_and_secret_deleted = $true
        keyboard_focusable_distinct_access_keys = $accessKeys.Count
        status_after_save = $savedStatusName
        status_after_revocation = $revokedStatusName
        operations_heading = $operationsHeading.Current.Name
        operations_fixture_boundary = $operationsBoundary.Current.Name
        operations_approval_default_unchecked = $true
        operations_processing_without_approval_denied = $true
        operations_keyboard_focusable_distinct_access_keys = $operationsAccessKeys.Count
        operations_summary_before = $operationsSummaryBefore
        operations_summary_after = $operationsSummaryAfter
        operations_status_after_process = $operationsCompletedStatusName
        blocked_case_review_before = $blockedDetailsBefore
        blocked_case_review_after = $blockedDetailsAfter
        blocked_case_approval_default_unchecked = $true
        blocked_case_without_approval_denied = $true
        blocked_case_status_after_disposition = $dispositionStatusName
        blocked_case_provider_calls = 0
        blocked_case_financial_mutations = 0
        blocked_case_remained_blocked = $true
        failed_payout_review_before = $failedPayoutDetailsBefore
        failed_payout_review_after = $failedPayoutDetailsAfter
        failed_payout_approval_default_unchecked = $true
        failed_payout_without_approval_denied = $true
        failed_payout_status_after_disposition = $failedPayoutStatusName
        failed_payout_provider_calls = 0
        failed_payout_financial_mutations = 0
        failed_payout_attempts = 0
        failed_payout_notification_transports = 0
        failed_payout_remained_held = $true
        failed_payout_replay_enabled = $false
        operations_provider_calls = 0
        operations_production_actions = 0
        contributor_dashboard_heading = $dashboardHeading.Current.Name
        contributor_dashboard_fixture_boundary = $dashboardBoundary.Current.Name
        contributor_dashboard_summary = $dashboardSummary
        contributor_dashboard_periods = $periodItems.Count
        contributor_dashboard_failed_payout_notices = $noticeItems.Count
        contributor_dashboard_notice_privacy_minimized = $true
        contributor_dashboard_statuses = @(
            "withheld", "held_below_threshold", "pending", "partially_paid", "paid")
        contributor_dashboard_local_status_notices = $true
        contributor_dashboard_external_notification_transports = 0
        contributor_dashboard_read_only = $true
        contributor_dashboard_network_requests = 0
        contributor_dashboard_payout_attempts = 0
        contributor_dashboard_status_after_refresh = $dashboardStatus.Current.Name
    } | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $evidencePath -Encoding utf8NoBOM

    "P11 recipient snapshot native UIA=$($heading.Current.Name)"
    "P11 recipient approval and revocation default-off=PASS"
    "P11 exact destination protected and metadata minimized=PASS"
    "P11 provider eligibility remains false=PASS"
    "P11 CARE operations default-off local cycle and focus=PASS"
    "P11 accessible blocked-case review and non-financial disposition=PASS"
    "P11 accessible failed-payout hold review and zero-authority disposition=PASS"
    "P11 contributor-facing period and failed-payout local notices=PASS external notification transports=0"
    "P11 accessible privacy-minimized contributor dashboard=PASS"
    "P11 CARE operations aggregate-only provider calls=0 production actions=0"
    "P11 recipient UI network requests=0 payout attempts=0"
    "P11 recipient UI evidence=$evidencePath"
}
finally {
    if ($null -ne $process -and -not $process.HasExited) {
        $null = $process.CloseMainWindow()
        if (-not $process.WaitForExit(5000)) { $process.Kill() }
    }
    try {
        if (Test-Path -LiteralPath $dataRoot -PathType Container) {
            $cleanupStore = [DCx.Core.Finance.PayoutRecipientSnapshotStore]::OpenAsync(
                $dataRoot, [Threading.CancellationToken]::None).GetAwaiter().GetResult()
            $cleanupSnapshots = $cleanupStore.ListAsync(
                [Threading.CancellationToken]::None).GetAwaiter().GetResult()
            foreach ($snapshot in @($cleanupSnapshots | Where-Object { $_.IsActive })) {
                $null = $cleanupStore.RevokeAsync(
                    $snapshot.SnapshotId,
                    $true,
                    [DateTimeOffset]::UtcNow,
                    [Threading.CancellationToken]::None).GetAwaiter().GetResult()
            }
        }
    }
    finally {
        $env:DCX_DATA_ROOT = $previousDataRoot
        $env:DCX_OFFLINE_STARTUP = $previousOfflineStartup
        $env:DCX_CARE_OPERATIONS_FIXTURE = $previousCareOperationsFixture
        $env:DCX_CARE_CONTRIBUTOR_DASHBOARD_FIXTURE = $previousCareContributorDashboardFixture
    }
}
