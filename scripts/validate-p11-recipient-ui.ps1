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

    [ordered]@{
        schema_version = 1
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
    } | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $evidencePath -Encoding utf8NoBOM

    "P11 recipient snapshot native UIA=$($heading.Current.Name)"
    "P11 recipient approval and revocation default-off=PASS"
    "P11 exact destination protected and metadata minimized=PASS"
    "P11 provider eligibility remains false=PASS"
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
    }
}
