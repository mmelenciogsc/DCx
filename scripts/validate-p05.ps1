[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$benchmark = Join-Path $repoRoot "tests\DCx.BossBenchmark\bin\Release\net10.0\DCx.BossBenchmark.exe"
$runRoot = Join-Path $repoRoot "artifacts\test-results\p05-$([Guid]::NewGuid().ToString('N'))"
$checkpoint = Join-Path $runRoot "boss-checkpoints.jsonl"

if (-not (Test-Path $benchmark)) {
    throw "Build the BOSS benchmark before running P05 validation."
}

New-Item -ItemType Directory -Path $runRoot -Force | Out-Null
$output = & $benchmark $checkpoint
if ($LASTEXITCODE -ne 0) {
    throw "BOSS benchmark failed with exit code $LASTEXITCODE."
}

$lines = @(Get-Content -LiteralPath $checkpoint)
if ($lines.Count -ne 4) {
    throw "Expected four append-only attempt checkpoints; found $($lines.Count)."
}
$records = @($lines | ForEach-Object { $_ | ConvertFrom-Json })
if (@($records | Where-Object { $_.failureCode -eq "evidence_missing" }).Count -ne 1) {
    throw "The missing-evidence retry checkpoint was not recorded exactly once."
}
if (@($records | Where-Object { $_.route -eq "OpenAi" }).Count -ne 0) {
    throw "The benchmark unexpectedly used the remote provider."
}
if (($output -join "`n") -notmatch "run_status=PASS;input_tokens=24;output_tokens=11") {
    throw "The benchmark did not report the expected measured token totals."
}

$output
"append_only_checkpoints=$($lines.Count)"
"missing_evidence_retries=1"
"remote_provider_calls=0"
"P05 deterministic-first orchestration validation=PASS"
