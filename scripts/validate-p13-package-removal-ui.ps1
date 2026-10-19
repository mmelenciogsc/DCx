[CmdletBinding()]
param(
    [string]$ExecutablePath,
    [string]$RunRoot)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$artifactsRoot = [IO.Path]::GetFullPath((Join-Path $repoRoot "artifacts")).TrimEnd('\')
$artifactsPrefix = $artifactsRoot + '\'
if ([string]::IsNullOrWhiteSpace($ExecutablePath)) {
    $ExecutablePath = Join-Path $repoRoot "src\DCx.Desktop\bin\Release\net10.0-windows10.0.19041.0\DCx.exe"
}
if ([string]::IsNullOrWhiteSpace($RunRoot)) {
    $RunRoot = Join-Path $repoRoot "artifacts\test-results\p13-package-removal-$([Guid]::NewGuid().ToString('N'))"
    New-Item -ItemType Directory -Path $RunRoot | Out-Null
}

$executable = [IO.Path]::GetFullPath($ExecutablePath)
$run = [IO.Path]::GetFullPath($RunRoot)
$fixtureRoot = Join-Path $run "package-removal-ui"
$normalData = Join-Path $fixtureRoot "normal-data"
$evidencePath = Join-Path $fixtureRoot "validation.json"
$repoPrefix = [IO.Path]::GetFullPath($repoRoot).TrimEnd('\') + '\'
if (-not (Test-Path -LiteralPath $executable -PathType Leaf) -or
    -not $executable.StartsWith($repoPrefix, [StringComparison]::OrdinalIgnoreCase) -or
    -not $run.StartsWith($artifactsPrefix, [StringComparison]::OrdinalIgnoreCase) -or
    -not (Test-Path -LiteralPath $run -PathType Container) -or
    (Test-Path -LiteralPath $fixtureRoot) -or
    [IO.Path]::GetPathRoot($run).TrimEnd('\') -eq $run.TrimEnd('\')) {
    throw "The package-removal validation paths are invalid, unsafe, or already exist."
}
for ($current = Get-Item -LiteralPath $run -Force; $null -ne $current; $current = $current.Parent) {
    if (($current.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "The package-removal validation path crosses a reparse boundary."
    }
    if ($current.FullName.TrimEnd('\').Equals($artifactsRoot, [StringComparison]::OrdinalIgnoreCase)) {
        break
    }
}
New-Item -ItemType Directory -Path $fixtureRoot | Out-Null

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

function Wait-For([scriptblock]$condition, [string]$failureMessage, [int]$attempts = 80) {
    for ($attempt = 0; $attempt -lt $attempts; $attempt++) {
        $result = & $condition
        if ($result) { return $result }
        Start-Sleep -Milliseconds 200
    }
    throw $failureMessage
}

function Find-WindowByProcessAndId([int]$processId, [string]$automationId) {
    $processCondition = [Windows.Automation.PropertyCondition]::new(
        [Windows.Automation.AutomationElement]::ProcessIdProperty,
        $processId)
    $idCondition = [Windows.Automation.PropertyCondition]::new(
        [Windows.Automation.AutomationElement]::AutomationIdProperty,
        $automationId)
    $condition = [Windows.Automation.AndCondition]::new($processCondition, $idCondition)
    return [Windows.Automation.AutomationElement]::RootElement.FindFirst(
        [Windows.Automation.TreeScope]::Descendants,
        $condition)
}

function Get-MainWindow([int]$processId) {
    $condition = [Windows.Automation.PropertyCondition]::new(
        [Windows.Automation.AutomationElement]::ProcessIdProperty,
        $processId)
    return [Windows.Automation.AutomationElement]::RootElement.FindFirst(
        [Windows.Automation.TreeScope]::Children,
        $condition)
}

function Find-ById($root, [string]$automationId) {
    $condition = [Windows.Automation.PropertyCondition]::new(
        [Windows.Automation.AutomationElement]::AutomationIdProperty,
        $automationId)
    return $root.FindFirst([Windows.Automation.TreeScope]::Descendants, $condition)
}

function Invoke-WithKeyboardFocus($element, [string]$controlName) {
    $element.SetFocus()
    $null = Wait-For { $element.Current.HasKeyboardFocus } "$controlName did not receive keyboard focus."
    $element.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
}

function Start-Dcx([string[]]$arguments) {
    $start = [Diagnostics.ProcessStartInfo]::new($executable)
    $start.UseShellExecute = $false
    foreach ($argument in $arguments) { $null = $start.ArgumentList.Add($argument) }
    $started = [Diagnostics.Process]::Start($start)
    if ($null -eq $started) { throw "DCx did not start." }
    return $started
}

function Stop-Dcx([Diagnostics.Process]$candidate) {
    if ($null -eq $candidate -or $candidate.HasExited) { return }
    $null = $candidate.CloseMainWindow()
    if (-not $candidate.WaitForExit(5000)) { $candidate.Kill($true) }
}

function Get-ChildProcesses([int]$parentProcessId) {
    return @(Get-CimInstance Win32_Process -Filter "ParentProcessId = $parentProcessId")
}

$previousDataRoot = $env:DCX_DATA_ROOT
$previousFixture = $env:DCX_PACKAGE_REMOVAL_FIXTURE
$previousOfflineStartup = $env:DCX_OFFLINE_STARTUP
$deniedProcess = $null
$fixtureProcess = $null
$normalProcess = $null
$normalRecoveryProcess = $null
try {
    $env:DCX_PACKAGE_REMOVAL_FIXTURE = $null
    $deniedProcess = Start-Dcx @("--package-removal-fixture")
    if (-not $deniedProcess.WaitForExit(5000) -or $deniedProcess.ExitCode -ne 2) {
        throw "The package-removal fixture did not deny an ungated launch."
    }
    $deniedProcess = $null

    $env:DCX_PACKAGE_REMOVAL_FIXTURE = "1"
    $fixtureProcess = Start-Dcx @("--package-removal-fixture")
    $window = Wait-For {
        Find-WindowByProcessAndId $fixtureProcess.Id "PackageRemovalWindow"
    } "The package-removal validation window did not appear."
    $heading = Wait-For { Find-ById $window "PackageRemovalHeading" } "The uninstall heading was not exposed."
    $boundary = Wait-For { Find-ById $window "PackageRemovalBoundary" } "The uninstall security boundary was not exposed."
    $details = Wait-For { Find-ById $window "PackageRemovalReviewDetails" } "The exact uninstall review was not exposed."
    $consequences = Wait-For { Find-ById $window "PackageRemovalConsequencesApproval" } "The consequences acknowledgment was not exposed."
    $backup = Wait-For { Find-ById $window "PackageRemovalBackupApproval" } "The backup acknowledgment was not exposed."
    $openRecovery = Wait-For { Find-ById $window "PackageRemovalOpenRecoveryButton" } "The backup and recovery handoff was not exposed."
    $execute = Wait-For { Find-ById $window "ExecutePackageRemovalButton" } "The validation action was not exposed."
    $close = Wait-For { Find-ById $window "ClosePackageRemovalButton" } "The close action was not exposed."
    $status = Wait-For { Find-ById $window "PackageRemovalStatus" } "The assertive uninstall status was not exposed."

    $null = Wait-For { $heading.Current.HasKeyboardFocus } "The uninstall heading did not receive initial keyboard focus."
    if ($heading.Current.Name -ne "Uninstall DCx heading" -or
        $boundary.Current.Name -notlike "Deterministic local validation fixture*cannot invoke PackageManager*production action.*") {
        throw "The validation security boundary was not exact."
    }
    $reviewText = $details.GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).Current.Value
    foreach ($expected in @(
        "Requested operation: msix-current-user-uninstall",
        "Windows.Management.Deployment.PackageManager.RemovePackageAsync",
        "Removal options: None",
        "Removal scope: Current Windows user",
        "Requests elevation: no",
        "DCx.AgenticBrowser.ValidationFixture",
        "CN=DCx Synthetic Validation Fixture",
        "Exact review SHA-256:",
        "Review expires UTC:",
        "Windows may close DCx: yes",
        "Cancellation after API entry: no",
        "Package application data preserved: no",
        "Unused dependencies may be removed: yes",
        "It excludes sessions, cookies, credentials, global replicas, and protected settings")) {
        if ($reviewText -notlike "*$expected*") {
            throw "The exact uninstall review omitted: $expected"
        }
    }
    if ([regex]::Matches($reviewText, "(?m)^Exact review SHA-256: [0-9a-f]{64}\r?$").Count -ne 1) {
        throw "The uninstall review did not expose one canonical review digest."
    }

    $consequencesToggle = [Windows.Automation.TogglePattern]$consequences.GetCurrentPattern(
        [Windows.Automation.TogglePattern]::Pattern)
    $backupToggle = [Windows.Automation.TogglePattern]$backup.GetCurrentPattern(
        [Windows.Automation.TogglePattern]::Pattern)
    if ($consequencesToggle.Current.ToggleState -ne [Windows.Automation.ToggleState]::Off -or
        $backupToggle.Current.ToggleState -ne [Windows.Automation.ToggleState]::Off) {
        throw "An uninstall acknowledgment did not default to unchecked."
    }
    $accessKeys = @(
        $consequences.Current.AccessKey,
        $backup.Current.AccessKey,
        $openRecovery.Current.AccessKey,
        $execute.Current.AccessKey,
        $close.Current.AccessKey)
    if (@($accessKeys | Where-Object { [string]::IsNullOrWhiteSpace($_) }).Count -ne 0 -or
        @($accessKeys | Select-Object -Unique).Count -ne 5) {
        throw "The uninstall workflow did not expose five distinct access keys."
    }

    Invoke-WithKeyboardFocus $execute "The uninstall validation action"
    $null = Wait-For {
        $status.Current.Name -eq "Uninstall was not started. Acknowledge the close, noncancellation, data, and dependency consequences first."
    } "Missing consequences acknowledgment did not fail closed."
    $null = Wait-For { $consequences.Current.HasKeyboardFocus } "Denial did not focus the consequences acknowledgment."

    $consequencesToggle.Toggle()
    Invoke-WithKeyboardFocus $execute "The uninstall validation action"
    $null = Wait-For {
        $status.Current.Name -eq "Uninstall was not started. Acknowledge your separate backup choice first."
    } "Missing backup acknowledgment did not fail closed."
    $null = Wait-For { $backup.Current.HasKeyboardFocus } "Denial did not focus the backup acknowledgment."

    $backupToggle.Toggle()
    Invoke-WithKeyboardFocus $execute "The uninstall validation action"
    $expectedStatus = "Uninstall interaction validation complete. Removal permitted no. PackageManager invocations 0. Removal completed no. Application close expected no. Installer processes 0. Elevation requests 0. Production actions 0."
    $null = Wait-For { $status.Current.Name -eq $expectedStatus } "The zero-authority uninstall validation did not complete."
    if ($consequences.Current.IsEnabled -or $backup.Current.IsEnabled -or $execute.Current.IsEnabled -or
        $consequencesToggle.Current.ToggleState -ne [Windows.Automation.ToggleState]::Off -or
        $backupToggle.Current.ToggleState -ne [Windows.Automation.ToggleState]::Off) {
        throw "The one-use uninstall validation remained reusable or retained approval."
    }
    if (@(Get-ChildProcesses $fixtureProcess.Id).Count -ne 0) {
        throw "The uninstall validation fixture started a child process."
    }
    Invoke-WithKeyboardFocus $close "The uninstall close action"
    if (-not $fixtureProcess.WaitForExit(5000)) { throw "The uninstall validation fixture did not close." }
    $fixtureProcess = $null

    $env:DCX_PACKAGE_REMOVAL_FIXTURE = $null
    $env:DCX_DATA_ROOT = $normalData
    $env:DCX_OFFLINE_STARTUP = "1"
    $normalProcess = Start-Dcx @()
    $mainWindow = Wait-For { Get-MainWindow $normalProcess.Id } "The normal DCx window did not appear."
    $mainStatus = Wait-For { Find-ById $mainWindow "StatusText" } "The normal DCx status was not exposed."
    $null = Wait-For {
        $mainStatus.Current.Name.StartsWith("DCx is ready offline.", [StringComparison]::Ordinal)
    } "Normal DCx did not reach its offline-ready state."
    $childrenBefore = @(Get-ChildProcesses $normalProcess.Id)
    $childIdsBefore = @($childrenBefore | ForEach-Object { [int]$_.ProcessId })
    $openRemoval = Wait-For { Find-ById $mainWindow "PackageRemovalButton" } "The normal uninstall review launcher was not exposed."
    Invoke-WithKeyboardFocus $openRemoval "The normal uninstall review launcher"
    $normalRemovalWindow = Wait-For {
        Find-WindowByProcessAndId $normalProcess.Id "PackageRemovalWindow"
    } "The normal uninstall review did not open."
    $normalBoundary = Wait-For { Find-ById $normalRemovalWindow "PackageRemovalBoundary" } "The normal uninstall boundary was not exposed."
    $normalConsequences = Find-ById $normalRemovalWindow "PackageRemovalConsequencesApproval"
    $normalBackup = Find-ById $normalRemovalWindow "PackageRemovalBackupApproval"
    $normalOpenRecovery = Find-ById $normalRemovalWindow "PackageRemovalOpenRecoveryButton"
    $normalExecute = Find-ById $normalRemovalWindow "ExecutePackageRemovalButton"
    $normalStatus = Find-ById $normalRemovalWindow "PackageRemovalStatus"
    if ($normalBoundary.Current.Name -notlike "DCx is not running with Windows package identity*unavailable*" -or
        $normalConsequences.Current.IsEnabled -or $normalBackup.Current.IsEnabled -or
        $normalExecute.Current.IsEnabled -or -not $normalOpenRecovery.Current.IsEnabled -or
        $normalStatus.Current.Name -ne "Uninstall is unavailable in this build. PackageManager invocations 0. Application close requests 0. Production actions 0.") {
        throw "The normal unpackaged uninstall surface did not fail closed."
    }
    Start-Sleep -Milliseconds 500
    $unexpectedChildren = @(Get-ChildProcesses $normalProcess.Id | Where-Object {
        $childIdsBefore -notcontains [int]$_.ProcessId -and
        -not $_.Name.Equals("msedgewebview2.exe", [StringComparison]::OrdinalIgnoreCase)
    })
    if ($unexpectedChildren.Count -ne 0) {
        throw "The normal uninstall review started an unexpected non-WebView child process."
    }

    $handoffChildrenBefore = @(Get-ChildProcesses $normalProcess.Id)
    $handoffChildIdsBefore = @($handoffChildrenBefore | ForEach-Object { [int]$_.ProcessId })
    Invoke-WithKeyboardFocus $normalOpenRecovery "The backup and recovery handoff"
    $handoffStatus = "Backup and recovery opened separately. No backup was created, DCx was not closed, and uninstall was not started. Close the main DCx window before creating a backup."
    $null = Wait-For { $normalStatus.Current.Name -eq $handoffStatus } "The backup handoff did not report its exact zero-action result."
    $recoveryChild = Wait-For {
        @(Get-ChildProcesses $normalProcess.Id | Where-Object {
            $handoffChildIdsBefore -notcontains [int]$_.ProcessId -and
            $_.Name.Equals([IO.Path]::GetFileName($executable), [StringComparison]::OrdinalIgnoreCase)
        }) | Select-Object -First 1
    } "The exact child DCx recovery process did not start."
    if (-not [string]::Equals(
            [IO.Path]::GetFullPath([string]$recoveryChild.ExecutablePath),
            $executable,
            [StringComparison]::OrdinalIgnoreCase) -or
        ([string]$recoveryChild.CommandLine -ne ('"' + $executable + '" --recovery') -and
         [string]$recoveryChild.CommandLine -ne ($executable + ' --recovery'))) {
        throw "The backup handoff child did not use the exact current executable and recovery argument."
    }
    $normalRecoveryProcess = [Diagnostics.Process]::GetProcessById([int]$recoveryChild.ProcessId)
    $recoveryWindow = Wait-For {
        Find-WindowByProcessAndId $normalRecoveryProcess.Id "RecoveryWindow"
    } "The separate backup and recovery window did not appear."
    $recoveryPath = Wait-For { Find-ById $recoveryWindow "RecoveryBackupPath" } "The backup path control was not exposed."
    $null = Wait-For { $recoveryPath.Current.HasKeyboardFocus } "The separate recovery window did not receive its defined initial focus."
    if ($normalProcess.HasExited -or $normalRecoveryProcess.HasExited -or
        $recoveryWindow.Current.Name -ne "DCx backup and recovery window") {
        throw "The backup handoff closed DCx or did not open the exact recovery window."
    }
    Stop-Dcx $normalRecoveryProcess
    $normalRecoveryProcess = $null

    (Find-ById $normalRemovalWindow "ClosePackageRemovalButton").GetCurrentPattern(
        [Windows.Automation.InvokePattern]::Pattern).Invoke()
    $null = Wait-For {
        $null -eq (Find-WindowByProcessAndId $normalProcess.Id "PackageRemovalWindow")
    } "The normal uninstall review did not close."

    $evidence = [ordered]@{
        captured_at_utc = (Get-Date).ToUniversalTime().ToString("O")
        fixture_gate_denied_without_environment = $true
        heading_keyboard_focus = $true
        exact_review_exposed = $true
        exact_review_digest_count = 1
        distinct_access_keys = $accessKeys
        consequences_acknowledgment_initially_unchecked = $true
        backup_acknowledgment_initially_unchecked = $true
        denial_invocations_originated_with_keyboard_focus = $true
        consequences_denial_focus = $true
        backup_denial_focus = $true
        acknowledgments_reset = $true
        execution_status = "validation_no_op"
        execution_permitted = $false
        package_manager_invocations = 0
        removal_completed = $false
        application_close_expected = $false
        installer_processes_started = 0
        elevation_requests = 0
        child_processes_started = 0
        production_actions = 0
        normal_product_source = "unavailable"
        normal_product_availability = "not_packaged"
        normal_product_controls_disabled = $true
        normal_product_recovery_handoff_enabled = $true
        normal_product_recovery_process_starts = 1
        normal_product_recovery_targets_current_executable = $true
        normal_product_recovery_exact_argument = $true
        normal_product_recovery_shell_execution = $false
        normal_product_recovery_backup_created = $false
        normal_product_recovery_application_close_requests = 0
        normal_product_recovery_installer_processes_started = 0
        normal_product_recovery_elevation_requests = 0
        normal_product_recovery_production_actions = 0
        normal_product_package_manager_invocations = 0
        normal_product_application_close_requests = 0
        normal_product_production_actions = 0
    }
    $evidence | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $evidencePath -Encoding utf8

    "P13 package-removal fixture gate=PASS"
    "P13 package-removal keyboard and UIA review=PASS"
    "P13 two default-off acknowledgments and denial focus=PASS"
    "P13 one-use uninstall validation no-op=PASS"
    "P13 package-removal PackageManager=0 child=0 close=0 elevation=0 production=0"
    "P13 exact backup/recovery handoff process=1; backup=0 close=0 installer=0 elevation=0 production=0"
    "P13 normal package-removal source=unavailable; availability=not_packaged"
    "P13 package-removal evidence=$evidencePath"
    [pscustomobject]$evidence
}
finally {
    Stop-Dcx $deniedProcess
    Stop-Dcx $fixtureProcess
    Stop-Dcx $normalRecoveryProcess
    Stop-Dcx $normalProcess
    $env:DCX_DATA_ROOT = $previousDataRoot
    $env:DCX_PACKAGE_REMOVAL_FIXTURE = $previousFixture
    $env:DCX_OFFLINE_STARTUP = $previousOfflineStartup
}
