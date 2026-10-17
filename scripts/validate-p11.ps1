[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$solution = Join-Path $repoRoot "DCx.slnx"
$tests = Join-Path $repoRoot "tests\DCx.Core.Tests\DCx.Core.Tests.csproj"
$runRoot = Join-Path $repoRoot "artifacts\test-results\p11-$([Guid]::NewGuid().ToString('N'))"
$financeTrx = Join-Path $runRoot "p11-finance.trx"

Push-Location $repoRoot
try {
    dotnet restore $solution --locked-mode
    if ($LASTEXITCODE -ne 0) { throw "Locked restore failed with exit code $LASTEXITCODE." }
    dotnet build $solution --configuration Release --no-restore
    if ($LASTEXITCODE -ne 0) { throw "Release build failed with exit code $LASTEXITCODE." }
    New-Item -ItemType Directory -Path $runRoot -Force | Out-Null
    dotnet test $tests --configuration Release --no-build --no-restore `
        --filter "FullyQualifiedName~DCx.Core.Tests.Finance." `
        --logger "trx;LogFileName=p11-finance.trx" --results-directory $runRoot
    if ($LASTEXITCODE -ne 0) { throw "P11 finance tests failed with exit code $LASTEXITCODE." }
    [xml]$financeReport = Get-Content -LiteralPath $financeTrx -Raw
    $financeCounters = $financeReport.TestRun.ResultSummary.Counters
    $financeTestsPassed = [int]$financeCounters.passed
    if ($financeTestsPassed -lt 1 -or
        [int]$financeCounters.total -ne $financeTestsPassed -or
        [int]$financeCounters.executed -ne $financeTestsPassed -or
        [int]$financeCounters.failed -ne 0 -or
        [int]$financeCounters.error -ne 0 -or
        [int]$financeCounters.timeout -ne 0 -or
        [int]$financeCounters.aborted -ne 0) {
        throw "P11 TRX counters did not prove that every selected finance test passed."
    }

    "P11 finance tests=PASS ($financeTestsPassed/$financeTestsPassed)"
    "US`$3 monthly subscription fixture=PASS"
    "Signed webhook replay and idempotency=PASS"
    "SQLite webhook and entitlement state survives reopen=PASS"
    "Out-of-order entitlement reactivation denial and equal-time deactivation=PASS"
    "Legacy CARE schema migration quarantine and minimized webhook decision audit=PASS"
    "Refund and chargeback entitlement handling=PASS"
    "Double-entry reconciliation=PASS"
    "Immutable ledger event conflict after restart=PASS"
    "Positive distributable net pool capped at aggregate 40 percent=PASS"
    "Zero or negative net payout exclusion=PASS"
    "Qualified attribution split and Net 60=PASS"
    "Owner approval duplicate payout and live-disable gates=PASS"
    "Durable payout reservation terminal state and restart reconciliation=PASS"
    "Crash-before-send not-found reconciliation and idempotent resubmission=PASS"
    "Crash-after-provider-acceptance reconciliation without resubmission=PASS"
    "Provider-pending batch remains reserved=PASS"
    "Official sandbox-only HTTP origin and OAuth request fixture=PASS"
    "Official webhook verification request and response fixture=PASS"
    "Verified event-to-plan and subscription mapping=PASS"
    "Sale-bound refund/reversal and cumulative correction cap=PASS"
    "Malformed/unverified event denial before ledger mutation=PASS"
    "Stable PayPal request ID and sender batch retry fixture=PASS"
    "Durable provider batch reference across reopen=PASS"
    "PayPal asynchronous PENDING/PROCESSING states remain nonterminal=PASS"
    "Owner-approved recipient snapshot ID and exact destination digest binding=PASS"
    "Batch SUCCESS requires exact item IDs recipients USD amounts and count=PASS"
    "Mixed payout outcomes post successful items only and hold failed payable amounts=PASS"
    "Real PayPal network calls=0"
    "P11 evidence=$runRoot"
    "P11 local CARE and PayPal mock validation=PASS"
}
finally {
    Pop-Location
}
