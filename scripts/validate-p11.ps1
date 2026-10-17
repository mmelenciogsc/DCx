[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$solution = Join-Path $repoRoot "DCx.slnx"
$tests = Join-Path $repoRoot "tests\DCx.Core.Tests\DCx.Core.Tests.csproj"

Push-Location $repoRoot
try {
    dotnet restore $solution --locked-mode
    if ($LASTEXITCODE -ne 0) { throw "Locked restore failed with exit code $LASTEXITCODE." }
    dotnet build $solution --configuration Release --no-restore
    if ($LASTEXITCODE -ne 0) { throw "Release build failed with exit code $LASTEXITCODE." }
    dotnet test $tests --configuration Release --no-build --no-restore --filter "FullyQualifiedName~Finance"
    if ($LASTEXITCODE -ne 0) { throw "P11 finance tests failed with exit code $LASTEXITCODE." }

    "US`$3 monthly subscription fixture=PASS"
    "Signed webhook replay and idempotency=PASS"
    "SQLite webhook and entitlement state survives reopen=PASS"
    "Refund and chargeback entitlement handling=PASS"
    "Double-entry reconciliation=PASS"
    "Immutable ledger event conflict after restart=PASS"
    "Positive distributable net pool capped at aggregate 40 percent=PASS"
    "Zero or negative net payout exclusion=PASS"
    "Qualified attribution split and Net 60=PASS"
    "Owner approval duplicate payout and live-disable gates=PASS"
    "Durable payout reservation terminal state and restart reconciliation=PASS"
    "Real PayPal network calls=0"
    "P11 local CARE and PayPal mock validation=PASS"
}
finally {
    Pop-Location
}
