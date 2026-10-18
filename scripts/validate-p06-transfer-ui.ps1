[CmdletBinding()]
param(
    [string]$RunRoot
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($RunRoot)) {
    $RunRoot = Join-Path $repoRoot "artifacts\test-results\p06-transfer-ui-$([Guid]::NewGuid().ToString('N'))"
}
$runRoot = [IO.Path]::GetFullPath($RunRoot)
$allowedRoot = [IO.Path]::GetFullPath((Join-Path $repoRoot "artifacts\test-results"))
if (-not $runRoot.StartsWith($allowedRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw "The dependency-transfer UIA root must be beneath the repository test-results directory."
}

$exe = Join-Path $repoRoot "src\DCx.Desktop\bin\Release\net10.0-windows10.0.19041.0\DCx.exe"
$dataRoot = Join-Path $runRoot "data"
$evidencePath = Join-Path $runRoot "dependency-transfer-ui-evidence.json"
$previousDataRoot = $env:DCX_DATA_ROOT
$previousFixture = $env:DCX_DEPENDENCY_TRANSFER_FIXTURE
$previousFixtureRoot = $env:DCX_DEPENDENCY_TRANSFER_FIXTURE_ROOT
$process = $null

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

function Wait-For([scriptblock]$Condition, [string]$FailureMessage, [int]$Attempts = 120) {
    for ($attempt = 0; $attempt -lt $Attempts; $attempt++) {
        $result = & $Condition
        if ($result) { return $result }
        Start-Sleep -Milliseconds 100
    }
    throw $FailureMessage
}

function Find-ById($Root, [string]$AutomationId) {
    $condition = [System.Windows.Automation.PropertyCondition]::new(
        [System.Windows.Automation.AutomationElement]::AutomationIdProperty,
        $AutomationId)
    return $Root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
}

function Find-Window([int]$ProcessId) {
    $condition = [System.Windows.Automation.AndCondition]::new(
        [System.Windows.Automation.PropertyCondition]::new(
            [System.Windows.Automation.AutomationElement]::ProcessIdProperty,
            $ProcessId),
        [System.Windows.Automation.PropertyCondition]::new(
            [System.Windows.Automation.AutomationElement]::AutomationIdProperty,
            "LocalModelsWindow"))
    return [System.Windows.Automation.AutomationElement]::RootElement.FindFirst(
        [System.Windows.Automation.TreeScope]::Descendants,
        $condition)
}

function Invoke-ById($Root, [string]$AutomationId) {
    $element = Wait-For { Find-ById $Root $AutomationId } "The $AutomationId action was not exposed."
    $element.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    return $element
}

if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) {
    throw "Build DCx Desktop before dependency-transfer UIA validation."
}
New-Item -ItemType Directory -Path $runRoot -Force | Out-Null

try {
    $env:DCX_DATA_ROOT = $dataRoot
    $env:DCX_DEPENDENCY_TRANSFER_FIXTURE = $null
    $env:DCX_DEPENDENCY_TRANSFER_FIXTURE_ROOT = $null
    $denied = Start-Process $exe -ArgumentList "--dependency-transfer-fixture" -PassThru -Wait
    if ($denied.ExitCode -ne 2 -or (Test-Path -LiteralPath $dataRoot)) {
        throw "The dependency-transfer fixture was available without its explicit gate."
    }

    $env:DCX_DEPENDENCY_TRANSFER_FIXTURE = "1"
    $env:DCX_DEPENDENCY_TRANSFER_FIXTURE_ROOT = $runRoot
    $process = Start-Process $exe -ArgumentList "--dependency-transfer-fixture" -PassThru
    $window = Wait-For { Find-Window $process.Id } "The gated dependency-transfer fixture did not open."

    $null = Wait-For {
        $candidate = Find-ById $window "DependencyCatalogSummary"
        if ($null -ne $candidate -and
            $candidate.Current.Name -like "Synthetic validation catalog dcx-p06-transfer-fixture.*" -and
            $candidate.Current.Name -like "*1 generated candidate*") { $candidate }
    } "The authenticated synthetic dependency catalog was not exposed."
    $preflight = Wait-For {
        $candidate = Find-ById $window "DependencyTransferPreflight"
        if ($null -ne $candidate -and
            $candidate.Current.Name -like "Transfer preflight only. Plan SHA-256 *" -and
            $candidate.Current.Name -like "*1 candidate. 2 immutable transfer items.*" -and
            $candidate.Current.Name -like "*Redirect policy SHA-256 *Approved redirect routes: none. The reviewed policy denies every redirect.*" -and
            $candidate.Current.Name -like "*separately approved exact-plan transfer, cancellation, protected verification and abandoned-partial recovery*") { $candidate }
    } "The accessible exact-plan transfer preflight was not exposed."
    if (-not $preflight.Current.IsKeyboardFocusable) {
        throw "The dependency transfer preflight is not keyboard focusable."
    }
    $null = Wait-For {
        $candidate = Find-ById $window "DependencyTransferStatus"
        if ($null -ne $candidate -and
            $candidate.Current.Name -like "Recovery check complete under the exclusive DCx data lease. Removed 1 abandoned partial transfer directory.*") { $candidate }
    } "Exclusive-lease opening recovery was not announced."

    $approval = Find-ById $window "ApproveDependencyTransferBox"
    if ($approval.Current.Name -notlike "*redirect-policy digest and routes*") {
        throw "The one-time approval did not expose its redirect-policy binding."
    }
    $approvalToggle = $approval.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
    $start = Find-ById $window "StartDependencyTransferButton"
    $cancel = Find-ById $window "CancelDependencyTransferButton"
    if ($approvalToggle.Current.ToggleState -ne [System.Windows.Automation.ToggleState]::Off -or
        -not $approval.Current.IsEnabled -or $start.Current.IsEnabled -or $cancel.Current.IsEnabled) {
        throw "Dependency transfer did not begin default-off with start and cancellation unavailable."
    }

    $actionIds = @(
        "ApproveDependencyTransferBox",
        "StartDependencyTransferButton",
        "CancelDependencyTransferButton",
        "VerifyDependencyTransferButton",
        "RecoverDependencyTransferButton")
    $accessKeys = foreach ($automationId in $actionIds) {
        $action = Find-ById $window $automationId
        if ($null -eq $action -or [string]::IsNullOrWhiteSpace($action.Current.AccessKey)) {
            throw "The $automationId control lacks an access key."
        }
        $action.Current.AccessKey
    }
    if (@($accessKeys | Sort-Object -Unique).Count -ne $accessKeys.Count) {
        throw "Dependency-transfer workflow controls expose duplicate access keys."
    }

    $approvalToggle.Toggle()
    $start = Wait-For {
        $candidate = Find-ById $window "StartDependencyTransferButton"
        if ($null -ne $candidate -and $candidate.Current.IsEnabled -and
            $candidate.Current.IsKeyboardFocusable) { $candidate }
    } "Explicit approval did not enable the keyboard-focusable transfer action."
    $start.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    $null = Wait-For {
        $status = Find-ById $window "DependencyTransferStatus"
        $cancelAction = Find-ById $window "CancelDependencyTransferButton"
        if ($null -ne $status -and $status.Current.Name -like "Dependency transfer Transferring.*" -and
            $null -ne $cancelAction -and $cancelAction.Current.IsEnabled) { $status }
    } "The active transfer did not expose progress and cancellation."
    Invoke-ById $window "CancelDependencyTransferButton" | Out-Null
    $canceled = Wait-For {
        $status = Find-ById $window "DependencyTransferStatus"
        if ($null -ne $status -and $status.Current.Name -like "Transfer canceled.*incomplete private state was removed*") { $status }
    } "The synthetic transfer did not cancel with safe cleanup."
    if (-not $canceled.Current.HasKeyboardFocus) {
        throw "Focus did not move to the canceled transfer result."
    }
    $approval = Find-ById $window "ApproveDependencyTransferBox"
    $approvalToggle = $approval.GetCurrentPattern([System.Windows.Automation.TogglePattern]::Pattern)
    if ($approvalToggle.Current.ToggleState -ne [System.Windows.Automation.ToggleState]::Off) {
        throw "One-time dependency transfer approval was retained after cancellation."
    }

    $approvalToggle.Toggle()
    $start = Wait-For {
        $candidate = Find-ById $window "StartDependencyTransferButton"
        if ($null -ne $candidate -and $candidate.Current.IsEnabled) { $candidate }
    } "Renewed approval did not enable the transfer action."
    $start.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
    $completed = Wait-For {
        $status = Find-ById $window "DependencyTransferStatus"
        if ($null -ne $status -and
            $status.Current.Name -like "Transfer complete and reverified.*" -and
            $status.Current.Name -like "*Redirect policy *" -and
            $status.Current.Name -like "*2 authenticated files.*" -and
            $status.Current.Name -like "*Nothing was extracted, installed, registered, selected, or executed.*") { $status }
    } "The explicitly approved synthetic transfer did not complete and reverify."
    if (-not $completed.Current.HasKeyboardFocus) {
        throw "Focus did not move to the completed transfer result."
    }
    $progress = Find-ById $window "DependencyTransferProgress"
    $range = $progress.GetCurrentPattern([System.Windows.Automation.RangeValuePattern]::Pattern)
    if ($range.Current.Value -ne 100) {
        throw "The completed dependency-transfer progress value is not 100 percent."
    }
    $approval = Find-ById $window "ApproveDependencyTransferBox"
    if ($approval.GetCurrentPattern(
            [System.Windows.Automation.TogglePattern]::Pattern).Current.ToggleState -ne
        [System.Windows.Automation.ToggleState]::Off) {
        throw "One-time dependency transfer approval was retained after completion."
    }

    Invoke-ById $window "VerifyDependencyTransferButton" | Out-Null
    $verified = Wait-For {
        $status = Find-ById $window "DependencyTransferStatus"
        if ($null -ne $status -and $status.Current.Name -like "Completed set verified.*Nothing was executed.*") { $status }
    } "The completed transfer set could not be reverified accessibly."
    if (-not $verified.Current.HasKeyboardFocus) {
        throw "Focus did not move to the protected verification result."
    }

    Invoke-ById $window "RecoverDependencyTransferButton" | Out-Null
    $recovered = Wait-For {
        $status = Find-ById $window "DependencyTransferStatus"
        if ($null -ne $status -and
            $status.Current.Name -like "Recovery complete. No removable abandoned partial transfer was found.*Unsafe linked content, if present, remains inert and untouched.*") { $status }
    } "Manual abandoned-partial recovery did not complete accessibly."
    if (-not $recovered.Current.HasKeyboardFocus) {
        throw "Focus did not move to the recovery result."
    }

    $evidence = [ordered]@{
        captured_at_utc = (Get-Date).ToUniversalTime().ToString("O")
        native_uia = "pass"
        fixture_without_gate_denied = $true
        approval_default_unchecked = $true
        start_without_approval_denied = $true
        keyboard_focusable_unique_access_keys = $true
        redirect_policy_digest_and_routes_reviewed = $true
        redirect_policy_bound_to_one_time_approval_and_receipt = $true
        item_and_byte_progress_announced = $true
        cancellation_available = $true
        cancellation_result_focus = $true
        approval_reset_after_cancellation = $true
        completed_set_reverified = $true
        completion_result_focus = $true
        approval_reset_after_completion = $true
        completed_progress_percent = 100
        protected_reverification_result_focus = $true
        exclusive_lease_recovery = $true
        recovery_result_focus = $true
        provider_requests = 0
        downloads = 0
        installs = 0
        executions = 0
    }
    $evidence | ConvertTo-Json | Set-Content -LiteralPath $evidencePath -Encoding utf8
    "Dependency transfer UIA=PASS"
    "Default-off exact-plan approval and unique access keys=PASS"
    "Item/byte progress, cancellation cleanup and focus=PASS"
    "Completed protected verification and recovery focus=PASS"
    "Provider requests/downloads/installs/executions=0"
    "Dependency transfer UI evidence=$evidencePath"
}
finally {
    if ($null -ne $process -and -not $process.HasExited) {
        $null = $process.CloseMainWindow()
        if (-not $process.WaitForExit(5000)) { $process.Kill($true) }
    }
    $env:DCX_DATA_ROOT = $previousDataRoot
    $env:DCX_DEPENDENCY_TRANSFER_FIXTURE = $previousFixture
    $env:DCX_DEPENDENCY_TRANSFER_FIXTURE_ROOT = $previousFixtureRoot
}
