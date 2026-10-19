[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$solution = Join-Path $repoRoot "DCx.slnx"
$tests = Join-Path $repoRoot "tests\DCx.Core.Tests\DCx.Core.Tests.csproj"
$reporter = Join-Path $repoRoot "tests\DCx.ReadinessReport\bin\Release\net10.0\DCx.ReadinessReport.dll"
$desktop = Join-Path $repoRoot "src\DCx.Desktop\bin\Release\net10.0-windows10.0.19041.0\DCx.exe"
$runRoot = Join-Path $repoRoot "artifacts\test-results\p14-$([Guid]::NewGuid().ToString('N'))"
$report = Join-Path $runRoot "go-no-go.json"
$readinessTrx = Join-Path $runRoot "p14-readiness.trx"
$uiaEvidencePath = Join-Path $runRoot "native-readiness-uia.json"
$previousDataRoot = $env:DCX_DATA_ROOT
$process = $null

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

function Wait-For([scriptblock]$condition, [string]$failureMessage, [int]$attempts = 50) {
    for ($attempt = 0; $attempt -lt $attempts; $attempt++) {
        $result = & $condition
        if ($result) { return $result }
        Start-Sleep -Milliseconds 200
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

function Find-ByName($root, [string]$name) {
    $condition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::NameProperty,
        $name)
    return $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
}

Push-Location $repoRoot
try {
    dotnet restore $solution --locked-mode
    if ($LASTEXITCODE -ne 0) { throw "Locked restore failed with exit code $LASTEXITCODE." }
    dotnet build $solution --configuration Release --no-restore
    if ($LASTEXITCODE -ne 0) { throw "Release build failed with exit code $LASTEXITCODE." }
    dotnet test $tests --configuration Release --no-build --no-restore `
        --filter "FullyQualifiedName~DCx.Core.Tests.Release." `
        --logger "trx;LogFileName=p14-readiness.trx" --results-directory $runRoot
    if ($LASTEXITCODE -ne 0) { throw "P14 readiness tests failed with exit code $LASTEXITCODE." }
    [xml]$readinessReport = Get-Content -LiteralPath $readinessTrx -Raw
    $readinessCounters = $readinessReport.TestRun.ResultSummary.Counters
    $readinessTestsPassed = [int]$readinessCounters.passed
    if ($readinessTestsPassed -lt 1 -or
        [int]$readinessCounters.total -ne $readinessTestsPassed -or
        [int]$readinessCounters.executed -ne $readinessTestsPassed -or
        [int]$readinessCounters.failed -ne 0 -or
        [int]$readinessCounters.error -ne 0 -or
        [int]$readinessCounters.timeout -ne 0 -or
        [int]$readinessCounters.aborted -ne 0) {
        throw "P14 TRX counters did not prove that every selected readiness test passed."
    }

    dotnet $reporter $report
    if ($LASTEXITCODE -ne 0) { throw "The readiness reporter failed with exit code $LASTEXITCODE." }
    $assessment = Get-Content -LiteralPath $report -Raw | ConvertFrom-Json
    if ($assessment.decision -ne "no_go" -or $assessment.production_activation_approved -ne $false -or
        $assessment.critical_and_major_issues -ne $assessment.critical_and_major_issues_triaged -or
        $assessment.blocking_critical_and_major_issues -le 0 -or $assessment.pilot_metrics.Count -ne 12) {
        throw "The P14 report did not preserve its NO_GO, triage, blocker, metric or activation gates."
    }

    $env:DCX_DATA_ROOT = Join-Path $runRoot "readiness-data"
    $process = Start-Process $desktop -ArgumentList "--readiness" -PassThru
    $window = Wait-For { Get-MainWindow $process.Id } "The native readiness dashboard did not start."
    $heading = Wait-For { Find-ById $window "ReadinessHeading" } "The readiness heading was not exposed through UIA."
    $decision = Wait-For {
        $element = Find-ById $window "ReadinessDecision"
        if ($null -ne $element -and $element.Current.Name.StartsWith("Decision NO GO.", [StringComparison]::Ordinal)) { $element }
    } "The NO_GO decision was not exposed through UIA."
    $summary = Wait-For {
        $element = Find-ById $window "ReadinessSummary"
        if ($null -ne $element -and $element.Current.Name -eq "7 blockers and 12 privacy-reviewed metric definitions are listed.") { $element }
    } "The blocker and metric counts were not exposed through UIA."
    $consent = Wait-For {
        $element = Find-ById $window "ReadinessConsentStatus"
        if ($null -ne $element -and
            $element.Current.Name.StartsWith("Local aggregate metric recording is disabled. 0 daily aggregate buckets are stored.", [StringComparison]::Ordinal)) { $element }
    } "The default-off metric consent state was not exposed through UIA."
    $defaultConsentName = $consent.Current.Name
    $pilotAdminPreview = Wait-For { Find-ById $window "PilotAdministrationPreview" } "The local pilot administration preview was not exposed through UIA."
    $pilotAdminStatus = Wait-For {
        $element = Find-ById $window "PilotAdministrationStatus"
        if ($null -ne $element -and
            $element.Current.Name -eq "Local pilot administration preview. Pilot enrollment unavailable. Product event sources 0. Network metric transports 0. Production activation unavailable.") { $element }
    } "The local pilot administration boundary was not exposed accurately through UIA."
    $pilotCohortPlanStatus = Wait-For {
        $element = Find-ById $window "PilotCohortPlanStatus"
        if ($null -ne $element -and
            $element.Current.Name -eq "Staged pilot plan preview. State blocked, not enrolled. 4 future stages. Enrollment controls 0. Cohort size and schedule are not approved.") { $element }
    } "The staged pilot plan did not preserve the blocked, not-enrolled boundary."
    $pilotCohortStages = Wait-For { Find-ById $window "PilotCohortStages" } "The staged pilot cohort list was not exposed through UIA."
    $cohortStageItems = $pilotCohortStages.FindAll(
        [System.Windows.Automation.TreeScope]::Descendants,
        (New-Object System.Windows.Automation.PropertyCondition(
            [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
            [System.Windows.Automation.ControlType]::ListItem)))
    if ($cohortStageItems.Count -ne 4) {
        throw "The staged pilot preview did not expose exactly four future cohort stages."
    }
    $pilotAggregateStatus = Wait-For {
        $element = Find-ById $window "PilotAggregateStatus"
        if ($null -ne $element -and
            $element.Current.Name -eq "0 retained aggregate buckets are listed for the last 30 UTC days. 0 retained aggregate buckets exist in total. No raw events or network transport are available.") { $element }
    } "The empty local aggregate administration summary was not exposed through UIA."
    $pilotAggregates = Wait-For { Find-ById $window "PilotAggregates" } "The local aggregate administration list was not exposed through UIA."
    $incidentDryRun = Wait-For { Find-ById $window "RunIncidentDryRun" } "The local incident stop drill action was not exposed through UIA."
    $incidentResults = Wait-For { Find-ById $window "IncidentDryRunResults" } "The local incident stop drill results were not exposed through UIA."
    $privacyControls = Wait-For { Find-ById $window "MetricPrivacyControls" } "The local metric privacy controls were not exposed through UIA."
    $optInApproval = Wait-For { Find-ById $window "MetricOptInApproval" } "The local metric opt-in approval was not exposed through UIA."
    $enableMetrics = Wait-For { Find-ById $window "EnableMetricRecording" } "The local metric enable action was not exposed through UIA."
    $optOutApproval = Wait-For { Find-ById $window "MetricOptOutApproval" } "The local metric stop-and-purge approval was not exposed through UIA."
    $stopAndPurge = Wait-For { Find-ById $window "StopAndPurgeMetrics" } "The local metric stop-and-purge action was not exposed through UIA."
    $consentActionStatus = Wait-For { Find-ById $window "MetricConsentActionStatus" } "The local metric privacy action status was not exposed through UIA."
    if ($optInApproval.Current.Name -notlike "*initially unchecked*" -or
        $optOutApproval.Current.Name -notlike "*initially unchecked*" -or
        $enableMetrics.Current.IsEnabled -or $stopAndPurge.Current.IsEnabled) {
        throw "The local metric privacy approvals were not initially unchecked and fail closed."
    }

    $optInToggle = [System.Windows.Automation.TogglePattern]$optInApproval.GetCurrentPattern(
        [System.Windows.Automation.TogglePattern]::Pattern)
    $optInToggle.Toggle()
    $enableMetrics = Wait-For {
        $element = Find-ById $window "EnableMetricRecording"
        if ($null -ne $element -and $element.Current.IsEnabled) { $element }
    } "Explicit local metric consent did not enable the bounded local action."
    ([System.Windows.Automation.InvokePattern]$enableMetrics.GetCurrentPattern(
        [System.Windows.Automation.InvokePattern]::Pattern)).Invoke()
    $enabledConsent = Wait-For {
        $element = Find-ById $window "ReadinessConsentStatus"
        if ($null -ne $element -and
            $element.Current.Name.StartsWith("Local aggregate metric recording is opted in under notice version 1. 0 daily aggregate buckets are stored.", [StringComparison]::Ordinal)) { $element }
    } "The local metric opt-in did not update the consent status."
    $enabledConsentName = $enabledConsent.Current.Name
    $enableResult = Wait-For {
        $element = Find-ById $window "MetricConsentActionStatus"
        if ($null -ne $element -and
            $element.Current.HasKeyboardFocus -and
            $element.Current.Name.StartsWith("Local aggregate recording is enabled under notice version 1.", [StringComparison]::Ordinal)) { $element }
    } "The local metric opt-in result was not focused and exposed through UIA."
    $optInApproval = Find-ById $window "MetricOptInApproval"
    $optInToggle = [System.Windows.Automation.TogglePattern]$optInApproval.GetCurrentPattern(
        [System.Windows.Automation.TogglePattern]::Pattern)
    if ($optInToggle.Current.ToggleState -ne [System.Windows.Automation.ToggleState]::Off) {
        throw "The one-use local metric opt-in approval did not reset."
    }

    $optOutApproval = Find-ById $window "MetricOptOutApproval"
    $optOutToggle = [System.Windows.Automation.TogglePattern]$optOutApproval.GetCurrentPattern(
        [System.Windows.Automation.TogglePattern]::Pattern)
    $optOutToggle.Toggle()
    $stopAndPurge = Wait-For {
        $element = Find-ById $window "StopAndPurgeMetrics"
        if ($null -ne $element -and $element.Current.IsEnabled) { $element }
    } "Explicit stop-and-purge approval did not enable the bounded local action."
    ([System.Windows.Automation.InvokePattern]$stopAndPurge.GetCurrentPattern(
        [System.Windows.Automation.InvokePattern]::Pattern)).Invoke()
    $disabledConsent = Wait-For {
        $element = Find-ById $window "ReadinessConsentStatus"
        if ($null -ne $element -and
            $element.Current.Name.StartsWith("Local aggregate metric recording is disabled. 0 daily aggregate buckets are stored.", [StringComparison]::Ordinal)) { $element }
    } "The local metric stop-and-purge action did not restore the disabled status."
    $disabledConsentName = $disabledConsent.Current.Name
    $purgeResult = Wait-For {
        $element = Find-ById $window "MetricConsentActionStatus"
        if ($null -ne $element -and
            $element.Current.HasKeyboardFocus -and
            $element.Current.Name.StartsWith("Local aggregate recording stopped. All retained local aggregate metrics were permanently deleted.", [StringComparison]::Ordinal)) { $element }
    } "The local metric stop-and-purge result was not focused and exposed through UIA."
    $optOutApproval = Find-ById $window "MetricOptOutApproval"
    $optOutToggle = [System.Windows.Automation.TogglePattern]$optOutApproval.GetCurrentPattern(
        [System.Windows.Automation.TogglePattern]::Pattern)
    if ($optOutToggle.Current.ToggleState -ne [System.Windows.Automation.ToggleState]::Off -or
        (Find-ById $window "StopAndPurgeMetrics").Current.IsEnabled) {
        throw "The one-use local metric stop-and-purge approval did not reset."
    }
    ([System.Windows.Automation.InvokePattern]$incidentDryRun.GetCurrentPattern(
        [System.Windows.Automation.InvokePattern]::Pattern)).Invoke()
    $incidentResult = Wait-For {
        $element = Find-ById $window "IncidentDryRunStatus"
        if ($null -ne $element -and $element.Current.HasKeyboardFocus -and
            $element.Current.Name -eq "Dry-run incident stop exercise passed for 7 scenarios. External actions 0. Pilot enrollment, metric transport and production activation remain unavailable. A fresh readiness decision is required before resumption.") { $element }
    } "The local incident stop drill did not expose its focused zero-authority result."
    $blockers = Wait-For { Find-ById $window "ReadinessBlockers" } "The blocker list was not exposed through UIA."
    $metrics = Wait-For { Find-ById $window "ReadinessMetrics" } "The metric-definition list was not exposed through UIA."
    $refresh = Wait-For { Find-ById $window "ReadinessRefresh" } "The refresh action was not exposed through UIA."
    $privacyAccessKeys = @(
        $optInApproval.Current.AccessKey,
        $enableMetrics.Current.AccessKey,
        $optOutApproval.Current.AccessKey,
        $stopAndPurge.Current.AccessKey,
        $incidentDryRun.Current.AccessKey,
        $refresh.Current.AccessKey)
    if ($privacyAccessKeys -contains "" -or
        @($privacyAccessKeys | Select-Object -Unique).Count -ne $privacyAccessKeys.Count) {
        throw "The local metric privacy and refresh access keys were missing or conflicting."
    }
    if ($null -ne (Find-ByName $window "Activate production") -or
        $null -ne (Find-ByName $window "Enroll pilot") -or
        $null -ne (Find-ByName $window "Send metrics")) {
        throw "The local pilot administration preview exposed enrollment, transport or production activation authority."
    }

    [ordered]@{
        schema_version = 3
        captured_at_utc = [DateTimeOffset]::UtcNow.ToString("O")
        fixture = "disposable local readiness data root"
        decision = "no_go"
        blockers = $assessment.blocking_critical_and_major_issues
        metric_definitions = $assessment.pilot_metrics.Count
        default_consent_status = $defaultConsentName
        enabled_consent_status = $enabledConsentName
        disabled_after_purge_status = $disabledConsentName
        opt_in_approval_initially_unchecked = $true
        opt_in_approval_reset = $true
        opt_out_approval_initially_unchecked = $true
        opt_out_approval_reset = $true
        enable_result_focused = $true
        purge_result_focused = $true
        distinct_access_keys = $privacyAccessKeys
        retained_aggregate_buckets_after_purge = 0
        local_pilot_administration_preview = $true
        staged_pilot_state = "blocked-not-enrolled"
        staged_pilot_future_cohorts = $cohortStageItems.Count
        staged_pilot_owner_approval_recorded = $false
        staged_pilot_size_and_schedule_approved = $false
        recent_aggregate_list_exposed = $true
        incident_dry_run_scenarios = 7
        incident_dry_run_result_focused = $true
        incident_dry_run_requires_fresh_readiness = $true
        pilot_enrollment_controls = 0
        product_event_sources = 0
        network_metric_transports = 0
        production_activation_control_exposed = $false
        production_actions = 0
    } | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $uiaEvidencePath -Encoding utf8NoBOM

    "P14 readiness tests=PASS ($readinessTestsPassed/$readinessTestsPassed)"
    "Critical and major issues triaged=$($assessment.critical_and_major_issues_triaged)/$($assessment.critical_and_major_issues)"
    "Blocking critical and major issues=$($assessment.blocking_critical_and_major_issues)"
    "Privacy-minimized pilot metrics=$($assessment.pilot_metrics.Count)"
    "Pilot metric recording default-off and opt-in=PASS"
    "Aggregate-only daily metric persistence=PASS"
    "Consent revocation purge=PASS"
    "Uncataloged metric and free-form dimension rejection=PASS"
    "Native readiness dashboard UIA=$($heading.Current.Name)"
    "Native readiness decision UIA=$($decision.Current.Name)"
    "Native readiness counts UIA=$($summary.Current.Name)"
    "Native readiness consent UIA=$defaultConsentName"
    "Native local consent enable UIA=$enabledConsentName"
    "Native local consent stop and purge UIA=$disabledConsentName"
    "Native local consent focused results=PASS"
    "Native local consent access keys=$($privacyAccessKeys.Count) distinct"
    "Native local pilot administration preview=PASS"
    "Staged pilot cohort preview=PASS (blocked-not-enrolled; 4 future stages; zero enrollment controls)"
    "Native recent aggregate administration list=PASS"
    "Native seven-scenario incident stop drill=PASS"
    "Native readiness blocker and metric lists=PASS"
    "Pilot enrollment metric transport and production activation controls absent=PASS"
    "Production activation approved=$($assessment.production_activation_approved)"
    "P14 decision=NO_GO"
    "P14 evidence=$report"
    "P14 native consent UIA evidence=$uiaEvidencePath"
}
finally {
    if ($null -ne $process -and -not $process.HasExited) {
        $null = $process.CloseMainWindow()
        if (-not $process.WaitForExit(5000)) { $process.Kill() }
    }
    $env:DCX_DATA_ROOT = $previousDataRoot
    Pop-Location
}
