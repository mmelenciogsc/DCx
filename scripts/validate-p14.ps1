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
        if ($null -ne $element -and $element.Current.Name.StartsWith("Local aggregate metric recording is disabled.", [StringComparison]::Ordinal)) { $element }
    } "The default-off metric consent state was not exposed through UIA."
    $blockers = Wait-For { Find-ById $window "ReadinessBlockers" } "The blocker list was not exposed through UIA."
    $metrics = Wait-For { Find-ById $window "ReadinessMetrics" } "The metric-definition list was not exposed through UIA."
    $refresh = Wait-For { Find-ById $window "ReadinessRefresh" } "The refresh action was not exposed through UIA."
    if ($null -ne (Find-ByName $window "Activate production")) {
        throw "The read-only readiness dashboard exposed a production activation action."
    }

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
    "Native readiness consent UIA=$($consent.Current.Name)"
    "Native readiness blocker and metric lists=PASS"
    "Production activation control absent=PASS"
    "Production activation approved=$($assessment.production_activation_approved)"
    "P14 decision=NO_GO"
    "P14 evidence=$report"
}
finally {
    if ($null -ne $process -and -not $process.HasExited) {
        $null = $process.CloseMainWindow()
        if (-not $process.WaitForExit(5000)) { $process.Kill() }
    }
    $env:DCX_DATA_ROOT = $previousDataRoot
    Pop-Location
}
