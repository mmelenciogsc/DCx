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
    $RunRoot = Join-Path $repoRoot "artifacts\test-results\p13-controlled-update-$([Guid]::NewGuid().ToString('N'))"
    New-Item -ItemType Directory -Path $RunRoot | Out-Null
}

$executable = [IO.Path]::GetFullPath($ExecutablePath)
$run = [IO.Path]::GetFullPath($RunRoot)
$fixtureRoot = Join-Path $run "controlled-update-ui"
$fixtureData = Join-Path $fixtureRoot "fixture-data"
$normalData = Join-Path $fixtureRoot "normal-data"
$evidencePath = Join-Path $fixtureRoot "validation.json"
$repoPrefix = [IO.Path]::GetFullPath($repoRoot).TrimEnd('\') + '\'
if (-not (Test-Path -LiteralPath $executable -PathType Leaf) -or
    -not $executable.StartsWith($repoPrefix, [StringComparison]::OrdinalIgnoreCase) -or
    -not $run.StartsWith($artifactsPrefix, [StringComparison]::OrdinalIgnoreCase) -or
    -not (Test-Path -LiteralPath $run -PathType Container) -or
    (Test-Path -LiteralPath $fixtureRoot) -or
    [IO.Path]::GetPathRoot($run).TrimEnd('\') -eq $run.TrimEnd('\')) {
    throw "The controlled-update validation paths are invalid, unsafe, or already exist."
}
for ($current = Get-Item -LiteralPath $run -Force; $null -ne $current; $current = $current.Parent) {
    if (($current.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "The controlled-update validation path crosses a reparse boundary."
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
$previousFixture = $env:DCX_CONTROLLED_UPDATE_FIXTURE
$previousFixtureRoot = $env:DCX_CONTROLLED_UPDATE_FIXTURE_ROOT
$previousOfflineStartup = $env:DCX_OFFLINE_STARTUP
$deniedProcess = $null
$fixtureProcess = $null
$normalProcess = $null
try {
    $env:DCX_DATA_ROOT = $fixtureData
    $env:DCX_CONTROLLED_UPDATE_FIXTURE_ROOT = $fixtureRoot
    $env:DCX_CONTROLLED_UPDATE_FIXTURE = $null
    $deniedProcess = Start-Dcx @("--controlled-update-fixture")
    if (-not $deniedProcess.WaitForExit(5000) -or $deniedProcess.ExitCode -ne 2) {
        throw "The controlled-update fixture did not deny an ungated launch."
    }
    $deniedProcess = $null

    $env:DCX_CONTROLLED_UPDATE_FIXTURE = "1"
    $fixtureProcess = Start-Dcx @("--controlled-update-fixture")
    $window = Wait-For {
        Find-WindowByProcessAndId $fixtureProcess.Id "ControlledUpdateWindow"
    } "The controlled-update validation window did not appear."
    $heading = Wait-For { Find-ById $window "ControlledUpdateHeading" } "The controlled-update heading was not exposed."
    $boundary = Wait-For { Find-ById $window "ControlledUpdateBoundary" } "The controlled-update security boundary was not exposed."
    $currentVersion = Wait-For { Find-ById $window "ControlledUpdateCurrentVersion" } "The current version was not exposed."
    $check = Wait-For { Find-ById $window "CheckControlledUpdateButton" } "The check action was not exposed."
    $details = Wait-For { Find-ById $window "ControlledUpdateReviewDetails" } "The exact review was not exposed."
    $approval = Wait-For { Find-ById $window "ApproveControlledUpdateStagingBox" } "The staging approval was not exposed."
    $stage = Wait-For { Find-ById $window "StageControlledUpdateButton" } "The staging action was not exposed."
    $close = Wait-For { Find-ById $window "CloseControlledUpdateButton" } "The controlled-update close action was not exposed."
    $status = Wait-For { Find-ById $window "ControlledUpdateStatus" } "The controlled-update live status was not exposed."

    $null = Wait-For { $heading.Current.HasKeyboardFocus } "The controlled-update heading did not receive initial keyboard focus."
    if ($heading.Current.Name -ne "Controlled updates heading" -or
        $boundary.Current.Name -notlike "Deterministic local validation fixture*Network requests*installer processes*production actions are unavailable.*" -or
        $currentVersion.Current.Name -ne "Current version: unavailable." -or
        $details.GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).Current.Value -ne "No update has been reviewed." -or
        $approval.Current.IsEnabled -or $stage.Current.IsEnabled) {
        throw "The initial controlled-update disclosure was not exact and fail closed."
    }
    $approvalToggle = [Windows.Automation.TogglePattern]$approval.GetCurrentPattern(
        [Windows.Automation.TogglePattern]::Pattern)
    if ($approvalToggle.Current.ToggleState -ne [Windows.Automation.ToggleState]::Off) {
        throw "Controlled-update staging approval did not default to unchecked."
    }
    if (@(Get-ChildProcesses $fixtureProcess.Id).Count -ne 0) {
        throw "The inert controlled-update window started a child process before review."
    }

    $check.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
    $null = Wait-For {
        $status.Current.Name -eq "Exact signed update review ready. Manifest requests 1. Package requests 0. Network requests 0. Nothing was staged or installed."
    } "The exact update review did not complete."
    $details = Find-ById $window "ControlledUpdateReviewDetails"
    $approval = Find-ById $window "ApproveControlledUpdateStagingBox"
    $stage = Find-ById $window "StageControlledUpdateButton"
    $reviewText = $details.GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).Current.Value
    foreach ($expected in @(
        "Current version: 1.0.0.0.",
        "Candidate version: 1.1.0.0.",
        "Manifest address: https://updates.fixture.invalid/stable/manifest.json.",
        "Package address: https://updates.fixture.invalid/stable/DCx-1.1.0.0.msix.",
        "Package identity: DCx.AgenticBrowser.",
        "Publisher: CN=DCx Controlled Update Fixture.",
        "Package SHA-256:",
        "Signed-envelope SHA-256:",
        "Exact review SHA-256:",
        "Approval authorizes inert staging only.")) {
        if ($reviewText -notlike "*$expected*") { throw "The exact update review omitted: $expected" }
    }
    $digests = [regex]::Matches($reviewText, '(?m)(?:Package|Signed-envelope|Exact review) SHA-256: ([0-9a-f]{64})\.')
    if ($digests.Count -ne 3 -or -not $approval.Current.IsEnabled -or -not $stage.Current.IsEnabled) {
        throw "The update review did not expose three canonical digests and enabled review controls."
    }
    $approvalToggle = [Windows.Automation.TogglePattern]$approval.GetCurrentPattern(
        [Windows.Automation.TogglePattern]::Pattern)
    if ($approvalToggle.Current.ToggleState -ne [Windows.Automation.ToggleState]::Off) {
        throw "Checking for an update changed the default-off staging approval."
    }
    $accessKeys = @($check.Current.AccessKey, $approval.Current.AccessKey, $stage.Current.AccessKey, $close.Current.AccessKey)
    if ($accessKeys -contains "" -or @($accessKeys | Select-Object -Unique).Count -ne 4) {
        throw "The controlled-update actions do not have four distinct keyboard access keys."
    }

    $stage.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
    $null = Wait-For {
        $status.Current.Name -eq "Staging denied. Review the exact update and check the separate staging approval first."
    } "Unchecked controlled-update staging did not fail closed."
    $null = Wait-For { $approval.Current.HasKeyboardFocus } "Denied staging did not move keyboard focus to its approval."
    $stagingRoot = Join-Path $fixtureData "controlled-update-stage"
    if (Test-Path -LiteralPath $stagingRoot) {
        throw "Denied staging created controlled-update storage."
    }
    if (@(Get-ChildProcesses $fixtureProcess.Id).Count -ne 0) {
        throw "Denied controlled-update staging started a child process."
    }

    $approvalToggle.Toggle()
    $stage.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
    $expectedStatus = "Version 1.1.0.0 was staged inertly. Execution permitted no. Installer processes 0. Elevation requests 0. Activation attempts 0. Restart attempts 0. Production actions 0."
    $null = Wait-For { $status.Current.Name -eq $expectedStatus } "Approved inert staging did not complete with an exact status."
    $details = Find-ById $window "ControlledUpdateReviewDetails"
    $approval = Find-ById $window "ApproveControlledUpdateStagingBox"
    $stage = Find-ById $window "StageControlledUpdateButton"
    $check = Find-ById $window "CheckControlledUpdateButton"
    $completedText = $details.GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).Current.Value
    foreach ($expected in @(
        "Manifest requests 2.", "Package requests 1.", "Network requests 0.",
        "Execution permitted no.", "Installer processes 0.", "Elevation requests 0.",
        "Activation attempts 0.", "Restart attempts 0.", "Production actions 0.")) {
        if ($completedText -notlike "*$expected*") { throw "The inert staging receipt omitted: $expected" }
    }
    $approvalToggle = [Windows.Automation.TogglePattern]$approval.GetCurrentPattern(
        [Windows.Automation.TogglePattern]::Pattern)
    if ($approvalToggle.Current.ToggleState -ne [Windows.Automation.ToggleState]::Off -or
        $approval.Current.IsEnabled -or $stage.Current.IsEnabled -or -not $check.Current.IsEnabled) {
        throw "The exact staging approval remained reusable without a fresh review."
    }
    $stagedFiles = @(Get-ChildItem -LiteralPath $stagingRoot -File -Recurse)
    if ($stagedFiles.Count -ne 2 -or
        @($stagedFiles | Where-Object Extension -eq '.msix').Count -ne 1 -or
        @($stagedFiles | Where-Object Name -like '*.manifest.json').Count -ne 1) {
        throw "The inert staging directory did not contain exactly one package and one signed manifest."
    }
    if (@(Get-ChildProcesses $fixtureProcess.Id).Count -ne 0) {
        throw "Approved inert controlled-update staging started a child process."
    }

    $close.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
    if (-not $fixtureProcess.WaitForExit(5000)) { throw "The controlled-update fixture did not close." }
    $fixtureProcess = $null

    $env:DCX_CONTROLLED_UPDATE_FIXTURE = $null
    $env:DCX_CONTROLLED_UPDATE_FIXTURE_ROOT = $null
    $env:DCX_DATA_ROOT = $normalData
    $env:DCX_OFFLINE_STARTUP = "1"
    $normalProcess = Start-Dcx @()
    $mainWindow = Wait-For { Get-MainWindow $normalProcess.Id } "The normal DCx window did not start."
    $updates = Wait-For { Find-ById $mainWindow "ControlledUpdatesButton" } "The normal controlled-update launcher was not exposed."
    if ($updates.Current.Name -ne "Open controlled updates" -or -not $updates.Current.IsEnabled) {
        throw "The normal controlled-update launcher was not accessible."
    }
    $updates.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
    $unavailableWindow = Wait-For {
        Find-WindowByProcessAndId $normalProcess.Id "ControlledUpdateWindow"
    } "The normal unavailable controlled-update window did not open."
    $unavailableBoundary = Find-ById $unavailableWindow "ControlledUpdateBoundary"
    $unavailableCheck = Find-ById $unavailableWindow "CheckControlledUpdateButton"
    $unavailableStatus = Find-ById $unavailableWindow "ControlledUpdateStatus"
    if ($unavailableBoundary.Current.Name -notlike "Production update service is not configured*No update endpoint*production action is available.*") {
        throw "The normal controlled-update source did not disclose its unavailable boundary."
    }
    $unavailableCheck.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
    $null = Wait-For {
        $unavailableStatus.Current.Name -eq "Controlled updates remain unavailable. Manifest requests 0. Package requests 0. Network requests 0. Installer processes 0."
    } "The normal controlled-update source inferred state or did not fail closed."
    $unavailableDetails = Find-ById $unavailableWindow "ControlledUpdateReviewDetails"
    $unavailableApproval = Find-ById $unavailableWindow "ApproveControlledUpdateStagingBox"
    $unavailableStage = Find-ById $unavailableWindow "StageControlledUpdateButton"
    if ($unavailableDetails.GetCurrentPattern([Windows.Automation.ValuePattern]::Pattern).Current.Value -ne
            "No controlled-update source is configured. No update state is inferred." -or
        $unavailableApproval.Current.IsEnabled -or $unavailableStage.Current.IsEnabled) {
        throw "The normal unavailable source exposed an actionable or inferred update."
    }

    $result = [ordered]@{
        captured_at_utc = (Get-Date).ToUniversalTime().ToString("O")
        fixture_gate_denied_without_environment = $true
        heading_keyboard_focus = $true
        exact_review_exposed = $true
        exact_review_digest_count = 3
        distinct_access_keys = $accessKeys
        approval_initially_unchecked = $true
        unapproved_stage_denied_before_storage = $true
        approval_reset_and_replay_denied = $true
        staged_file_count = $stagedFiles.Count
        staged_package_count = 1
        staged_signed_manifest_count = 1
        manifest_requests = 2
        package_requests = 1
        network_requests = 0
        execution_permitted = $false
        installer_processes_started = 0
        elevation_requests = 0
        activation_attempts = 0
        restart_attempts = 0
        production_actions = 0
        normal_product_source = "not_configured"
        normal_product_manifest_requests = 0
        normal_product_package_requests = 0
        normal_product_update_state_inferred = $false
    }
    $result | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $evidencePath -Encoding utf8
    Write-Host "P13 controlled-update fixture gate=PASS"
    Write-Host "P13 controlled-update keyboard and UIA review=PASS"
    Write-Host "P13 exact review, unchecked approval denial and one-use inert staging=PASS"
    Write-Host "P13 controlled-update network=0 installer=0 elevation=0 activation=0 restart=0 production=0"
    Write-Host "P13 normal controlled-update source=not_configured; inferred update state=false"
    Write-Host "P13 controlled-update evidence=$evidencePath"
    [pscustomobject]$result
}
finally {
    Stop-Dcx $normalProcess
    Stop-Dcx $fixtureProcess
    if ($null -ne $deniedProcess -and -not $deniedProcess.HasExited) { $deniedProcess.Kill($true) }
    $env:DCX_DATA_ROOT = $previousDataRoot
    $env:DCX_CONTROLLED_UPDATE_FIXTURE = $previousFixture
    $env:DCX_CONTROLLED_UPDATE_FIXTURE_ROOT = $previousFixtureRoot
    $env:DCX_OFFLINE_STARTUP = $previousOfflineStartup
}
